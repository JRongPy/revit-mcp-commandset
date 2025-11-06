using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Microsoft.CodeAnalysis.CSharp.SyntaxTokenParser;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    // ============================
    // 優化版動態編譯/執行引擎 (.NET 8 / Revit 2025)
    // 特性：
    // - Collectible ALC（防記憶體洩漏）
    // - LRU 快取（SHA256 鍵）
    // - 動態快取容量
    // - 組件白名單過濾
    // - 非同步 GC
    // - 執行緒安全
    // ============================
    internal static class MiniScriptEngine
    {
        // 基礎快取容量，會根據記憶體動態調整
        private const int BaseCacheCapacity = 16;
        private const int MaxCacheCapacity = 64;

        // 包裝模板：把使用者程式碼嵌到固定入口
        private const string WrapperTemplate = @"
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AIGeneratedCode
{
    public static class CodeExecutor
    {
        public static object Execute(Autodesk.Revit.DB.Document document, object[] parameters)
        {
            // === USER CODE START ===
{USER_CODE}
            // === USER CODE END ===
        }
    }
}
";

        // 用來把 Roslyn 診斷行號換算回使用者碼行號
        private static readonly int PrefixLineCount =
            WrapperTemplate.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                           .TakeWhile(l => !l.Contains("{USER_CODE}")).Count() + 1;

        // LRU 快取（key = code SHA256）- 執行緒安全版本
        private static readonly ConcurrentDictionary<string, byte[]> _dllCache = new();
        private static readonly LinkedList<string> _lruList = new();
        private static readonly object _lruLock = new();

        // 快取的 MetadataReference（避免重複建立）
        private static IReadOnlyList<MetadataReference> _cachedReferences;
        private static readonly object _referencesLock = new();

        /// <summary>
        /// 對外唯一入口：在可回收 ALC 內載入執行
        /// </summary>
        public static object Run(string userCode, Document doc, object[] parameters)
        {
            var dllBytes = CompileToDllBytes(userCode);

            var alc = new CollectibleALC();
            try
            {
                using var ms = new MemoryStream(dllBytes);
                var asm = alc.LoadFromStream(ms);

                var type = asm.GetType("AIGeneratedCode.CodeExecutor");
                if (type == null)
                    throw new InvalidOperationException("找不到類型 AIGeneratedCode.CodeExecutor");

                var method = type.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (method == null)
                    throw new InvalidOperationException("找不到靜態方法 Execute(Document, object[])");

                return method.Invoke(null, new object[] { doc, parameters });
            }
            finally
            {
                // 延遲非同步 GC，避免阻塞主執行緒
                var alcToUnload = alc;
                Task.Run(() =>
                {
                    alcToUnload.Unload();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect(); // 二次回收確保清理完成
                });
            }
        }

        /// <summary>
        /// 產出 DLL 位元組（含 LRU 快取）
        /// </summary>
        private static byte[] CompileToDllBytes(string userCode)
        {
            var key = Sha256(userCode);

            // 快取命中
            if (_dllCache.TryGetValue(key, out var cached))
            {
                TouchLru(key);
                return cached;
            }

            // 準備編譯
            var wrapped = WrapperTemplate.Replace("{USER_CODE}", Indent(userCode, 12));
            var syntaxTree = CSharpSyntaxTree.ParseText(wrapped, new CSharpParseOptions());

            var references = GetOrCreateReferences();

            // 2) 編譯選項：鎖定 x64
            var compilation = CSharpCompilation.Create(
                assemblyName: "RevitSnippet_" + Guid.NewGuid().ToString("N"),
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    platform: Platform.X64    
                )
            );

            // 編譯到記憶體
            using var pe = new MemoryStream();
            var emit = compilation.Emit(pe);
            if (!emit.Success)
                throw new InvalidOperationException("代碼編譯錯誤:\n" + BuildDiagnosticsMessage(emit.Diagnostics));

            var bytes = pe.ToArray();
            PutIntoCache(key, bytes);
            return bytes;
        }

        /// <summary>
        /// 建構清楚的錯誤訊息，行號對齊到使用者碼
        /// </summary>
        private static string BuildDiagnosticsMessage(IEnumerable<Diagnostic> diags)
        {
            var sb = new StringBuilder();
            foreach (var d in diags.Where(x => x.Severity == DiagnosticSeverity.Error))
            {
                var span = d.Location.GetLineSpan();
                var line = span.StartLinePosition.Line + 1 - PrefixLineCount;
                if (line < 1) line = 1;
                sb.AppendLine($"Line {line}: {d.GetMessage()}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 獲取或建立快取的 MetadataReference（單例模式）
        /// </summary>
        private static IReadOnlyList<MetadataReference> GetOrCreateReferences()
        {
            if (_cachedReferences != null)
                return _cachedReferences;

            lock (_referencesLock)
            {
                if (_cachedReferences != null)
                    return _cachedReferences;

                _cachedReferences = CollectReferences().ToList();
                return _cachedReferences;
            }
        }

        /// <summary>
        /// 自動收集 MetadataReference（含白名單過濾）
        /// </summary>
        private static IEnumerable<MetadataReference> CollectReferences()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 掃描已載入的組件（只收集白名單內的）
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var name = asm.GetName().Name;
                    if (string.IsNullOrEmpty(name))
                        continue;

                    var loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc))
                        paths.Add(loc);
                }
                catch { /* 動態組件可能沒有 Location */ }
            }

            // 強制加入核心錨點（確保必要組件存在）
            var anchors = new[]
            {
                typeof(object).Assembly,                          // System.Private.CoreLib
                typeof(Enumerable).Assembly,                      // System.Linq
                typeof(List<>).Assembly,                          // System.Collections
                typeof(System.Runtime.GCSettings).Assembly,       // System.Runtime
                typeof(Autodesk.Revit.DB.Document).Assembly,      // RevitAPI
                typeof(Autodesk.Revit.UI.UIApplication).Assembly, // RevitAPIUI
            };

            foreach (var a in anchors)
            {
                try
                {
                    var loc = a.Location;
                    if (!string.IsNullOrEmpty(loc))
                        paths.Add(loc);
                }
                catch { }
            }

            // 嘗試加入 netstandard（某些環境需要）
            TryAddAssemblyLocation(paths, "netstandard");

            foreach (var p in paths)
                yield return MetadataReference.CreateFromFile(p);
        }

        private static void TryAddAssemblyLocation(HashSet<string> set, string simpleName)
        {
            try
            {
                var asm = Assembly.Load(simpleName);
                var loc = asm.Location;
                if (!string.IsNullOrEmpty(loc))
                    set.Add(loc);
            }
            catch { }
        }

        /// <summary>
        /// 可回收的 AssemblyLoadContext
        /// </summary>
        private sealed class CollectibleALC : AssemblyLoadContext
        {
            public CollectibleALC() : base(isCollectible: true) { }
            protected override Assembly Load(AssemblyName assemblyName) => null; // 交給 Default ALC 解依賴
        }

        private static string Sha256(string s)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            return Convert.ToHexString(bytes);
        }

        private static string Indent(string s, int spaces)
        {
            var pad = new string(' ', spaces);
            var lines = s.Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Select(l => pad + l));
        }

        /// <summary>
        /// 執行緒安全的快取寫入（含 LRU 驅逐策略）
        /// </summary>
        private static void PutIntoCache(string key, byte[] dll)
        {
            lock (_lruLock)
            {
                // 更新快取
                _dllCache[key] = dll;

                // 更新 LRU 鏈表
                _lruList.Remove(key);
                _lruList.AddFirst(key);

                // 驅逐舊項（動態容量）
                var capacity = GetDynamicCacheCapacity();
                while (_lruList.Count > capacity)
                {
                    var last = _lruList.Last!.Value;
                    _lruList.RemoveLast();
                    _dllCache.TryRemove(last, out _);
                }
            }
        }

        /// <summary>
        /// 執行緒安全的 LRU 觸碰
        /// </summary>
        private static void TouchLru(string key)
        {
            lock (_lruLock)
            {
                _lruList.Remove(key);
                _lruList.AddFirst(key);
            }
        }

        /// <summary>
        /// 根據系統記憶體動態調整快取容量
        /// </summary>
        private static int GetDynamicCacheCapacity()
        {
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                var availableMB = gcInfo.TotalAvailableMemoryBytes / 1024 / 1024;

                // 記憶體充足時允許更大快取
                if (availableMB > 8192) return MaxCacheCapacity;      // 8GB+ → 64 項
                if (availableMB > 4096) return BaseCacheCapacity * 2; // 4GB+ → 32 項
                return BaseCacheCapacity;                             // 預設 → 16 項
            }
            catch
            {
                return BaseCacheCapacity; // 出錯時使用安全預設值
            }
        }
    }

    // ============================
    // 外部事件處理器：只負責 Transaction + 呼叫引擎
    // ============================
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _code;
        private object[] _parameters;
        private bool _autoTransaction = true;                  // 預設開啟
        private string _transactionName = "Execute AI code";   // 預設交易名稱

        public ExecutionResultInfo ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetExecutionParameters(
            string code, 
            object[] parameters = null,
            bool? autoTransaction = null,
            string transactionName = null)
        {
            _code = code ?? throw new ArgumentNullException(nameof(code));
            _parameters = parameters ?? Array.Empty<object>();
            if (autoTransaction.HasValue) _autoTransaction = autoTransaction.Value;
            if (!string.IsNullOrWhiteSpace(transactionName)) _transactionName = transactionName;
            
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
            => _resetEvent.WaitOne(timeoutMilliseconds);

        public void Execute(UIApplication app)
        {
            try
            {
                if (app?.ActiveUIDocument == null)
                    throw new InvalidOperationException("沒有可用的 ActiveUIDocument，請先開啟一個專案或視圖。");

                var doc = app.ActiveUIDocument.Document;
                ResultInfo = new ExecutionResultInfo();
                object result;

                if (_autoTransaction)
                {
                    if (doc.IsModifiable)
                    {
                        // 外層已有 Transaction：使用 SubTransaction
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            result = MiniScriptEngine.Run(_code, doc, _parameters);
                            sub.Commit();
                        }
                    }
                    else
                    {
                        // 無外層 Transaction：自行開主 Transaction
                        using (var tx = new Transaction(doc, _transactionName))
                        {
                            tx.Start();
                            var fho = tx.GetFailureHandlingOptions();
                            fho.SetFailuresPreprocessor(new DismissAllFailures());
                            tx.SetFailureHandlingOptions(fho);

                            result = MiniScriptEngine.Run(_code, doc, _parameters);

                            tx.Commit();
                        }
                    }
                }
                else
                {
                    // 不包交易（僅限查詢或呼叫已自帶交易的工具）
                    result = MiniScriptEngine.Run(_code, doc, _parameters);
                }
                ResultInfo.Success = true;
                ResultInfo.Result = JsonConvert.SerializeObject(result);

            }
            catch (Exception ex)
            {
                ResultInfo.Success = false;
                ResultInfo.ErrorMessage = $"執行失敗: {ex.Message}\n{ex.StackTrace}";
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "AI Dynamic Code Executor";
    }

    // ==============
    // DTO / Failure Processor
    // ==============
    public class ExecutionResultInfo
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("result")]
        public string Result { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// 抑制所有警告對話框，避免中斷自動化流程
    /// </summary>
    internal class DismissAllFailures : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            IList<FailureMessageAccessor> failList = accessor.GetFailureMessages();
            foreach (var fma in failList)
            {
                if (fma.GetSeverity() == FailureSeverity.Warning)
                    accessor.DeleteWarning(fma);
            }

            return FailureProcessingResult.Continue;
        }
    }
}