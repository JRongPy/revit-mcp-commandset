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

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    // ============================
    // 優化版動態編譯/執行引擎 (.NET 8 / Revit 2025)
    // 變更重點：
    // - WrapperTemplate 新增 {USER_CODE_CLASSES} 插槽
    // - Run/CompileToDllBytes 接受 userClasses
    // - 診斷行號：動態計算 prefix 行數（AsyncLocal）
    // ============================
    internal static class MiniScriptEngine
    {
        private const int BaseCacheCapacity = 16;
        private const int MaxCacheCapacity = 64;

        // ★ 新：雙插槽樣板
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
{USER_CODE_CLASSES}
        public static object Execute(Autodesk.Revit.DB.Document document, object[] parameters)
        {
            // === USER CODE START ===
{USER_CODE}
            // === USER CODE END ===
        }
    }
}
";

        // ★ 新：每次編譯動態計算「使用者碼」起始行
        private static readonly AsyncLocal<int> _currentPrefixLineCount = new AsyncLocal<int>();

        // LRU 快取（key = SHA256(classes + code)）
        private static readonly ConcurrentDictionary<string, byte[]> _dllCache = new();
        private static readonly LinkedList<string> _lruList = new();
        private static readonly object _lruLock = new();

        private static IReadOnlyList<MetadataReference> _cachedReferences;
        private static readonly object _referencesLock = new();

        /// <summary>
        /// 對外入口：在可回收 ALC 內載入執行
        /// </summary>
        public static object Run(string userCode, Document doc, object[] parameters, string userClasses = null)
        {
            var dllBytes = CompileToDllBytes(userCode, userClasses);

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
                var alcToUnload = alc;
                Task.Run(() =>
                {
                    alcToUnload.Unload();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                });
            }
        }

        /// <summary>
        /// 產出 DLL 位元組（含 LRU 快取）
        /// </summary>
        private static byte[] CompileToDllBytes(string userCode, string userClasses)
        {
            userClasses ??= string.Empty;

            // ★ 新：快取鍵同時考慮 classes 與 code
            var key = Sha256(userClasses + "\n---\n" + userCode);

            if (_dllCache.TryGetValue(key, out var cached))
            {
                TouchLru(key);
                return cached;
            }

            // ★ 新：先置換 classes，保留 {USER_CODE} 作為錨點來計算前置行數
            var withClasses = WrapperTemplate.Replace("{USER_CODE_CLASSES}", Indent(userClasses, 8));

            // 找到 {USER_CODE} 所在行，計算 prefix 行數
            var lines = withClasses.Replace("\r\n", "\n").Split('\n');
            int prefixIdx = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("{USER_CODE}")) { prefixIdx = i + 1; break; }
            }
            _currentPrefixLineCount.Value = prefixIdx;

            // 最終置換成實際使用者碼
            var wrapped = withClasses.Replace("{USER_CODE}", Indent(userCode, 12));

            var syntaxTree = CSharpSyntaxTree.ParseText(wrapped, new CSharpParseOptions());
            var references = GetOrCreateReferences();

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

            using var pe = new MemoryStream();
            var emit = compilation.Emit(pe);
            if (!emit.Success)
                throw new InvalidOperationException("代碼編譯錯誤:\n" + BuildDiagnosticsMessage(emit.Diagnostics));

            var bytes = pe.ToArray();
            PutIntoCache(key, bytes);
            return bytes;
        }

        /// <summary>
        /// 建構清楚的錯誤訊息，行號對齊到「使用者碼」
        /// </summary>
        private static string BuildDiagnosticsMessage(IEnumerable<Diagnostic> diags)
        {
            var prefix = _currentPrefixLineCount.Value <= 0 ? 1 : _currentPrefixLineCount.Value;
            var sb = new StringBuilder();
            foreach (var d in diags.Where(x => x.Severity == DiagnosticSeverity.Error))
            {
                var span = d.Location.GetLineSpan();
                var line = span.StartLinePosition.Line + 1 - prefix;
                if (line < 1) line = 1;
                sb.AppendLine($"Line {line}: {d.GetMessage()}");
            }
            return sb.ToString();
        }

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

        private static IEnumerable<MetadataReference> CollectReferences()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var name = asm.GetName().Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    var loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc)) paths.Add(loc);
                }
                catch { }
            }

            var anchors = new[]
            {
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(List<>).Assembly,
                typeof(System.Runtime.GCSettings).Assembly,
                typeof(Autodesk.Revit.DB.Document).Assembly,
                typeof(Autodesk.Revit.UI.UIApplication).Assembly,
            };

            foreach (var a in anchors)
            {
                try
                {
                    var loc = a.Location;
                    if (!string.IsNullOrEmpty(loc)) paths.Add(loc);
                }
                catch { }
            }

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

        private sealed class CollectibleALC : AssemblyLoadContext
        {
            public CollectibleALC() : base(isCollectible: true) { }
            protected override Assembly Load(AssemblyName assemblyName) => null;
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
            var lines = (s ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            return string.Join("\n", lines.Select(l => pad + l));
        }

        private static void PutIntoCache(string key, byte[] dll)
        {
            lock (_lruLock)
            {
                _dllCache[key] = dll;
                _lruList.Remove(key);
                _lruList.AddFirst(key);

                var capacity = GetDynamicCacheCapacity();
                while (_lruList.Count > capacity)
                {
                    var last = _lruList.Last!.Value;
                    _lruList.RemoveLast();
                    _dllCache.TryRemove(last, out _);
                }
            }
        }

        private static void TouchLru(string key)
        {
            lock (_lruLock)
            {
                _lruList.Remove(key);
                _lruList.AddFirst(key);
            }
        }

        private static int GetDynamicCacheCapacity()
        {
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                var availableMB = gcInfo.TotalAvailableMemoryBytes / 1024 / 1024;
                if (availableMB > 8192) return MaxCacheCapacity;
                if (availableMB > 4096) return BaseCacheCapacity * 2;
                return BaseCacheCapacity;
            }
            catch
            {
                return BaseCacheCapacity;
            }
        }
    }

    // ============================
    // 外部事件處理器：加 classes 支援
    // ============================
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _code;
        private string _classes;                        // ★ 新增：承載 DTO / helper 類別
        private object[] _parameters;
        private bool _autoTransaction = true;
        private string _transactionName = "Execute AI code";

        public ExecutionResultInfo ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetExecutionParameters(
            string code,
            object[] parameters = null,
            bool? autoTransaction = null,
            string transactionName = null,
            string classes = null)                      // ★ 新增參數
        {
            _code = code ?? throw new ArgumentNullException(nameof(code));
            _classes = classes;                         // 可以為 null
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
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            result = MiniScriptEngine.Run(_code, doc, _parameters, _classes); // ★ 傳入 classes
                            sub.Commit();
                        }
                    }
                    else
                    {
                        using (var tx = new Transaction(doc, _transactionName))
                        {
                            tx.Start();
                            var fho = tx.GetFailureHandlingOptions();
                            fho.SetFailuresPreprocessor(new DismissAllFailures());
                            tx.SetFailureHandlingOptions(fho);

                            result = MiniScriptEngine.Run(_code, doc, _parameters, _classes); // ★ 傳入 classes

                            tx.Commit();
                        }
                    }
                }
                else
                {
                    result = MiniScriptEngine.Run(_code, doc, _parameters, _classes); // ★ 傳入 classes
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
