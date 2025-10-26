using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    // ============================
    // 強壯版動態編譯/執行引擎 (.NET 8 / Revit 2025)
    // ============================
    internal static class MiniScriptEngine
    {
        private const int CacheCapacity = 16;

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

        // LRU 快取（key = code SHA256）
        private static readonly ConcurrentDictionary<string, byte[]> _dllCache = new();
        private static readonly LinkedList<string> _lruList = new();
        private static readonly object _lruLock = new();

        // 對外唯一入口：在可回收 ALC 內載入執行
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
                alc.Unload();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        // 產出 DLL 位元組（含 LRU 快取）
        private static byte[] CompileToDllBytes(string userCode)
        {
            var key = Sha256(userCode);
            if (_dllCache.TryGetValue(key, out var cached))
            {
                TouchLru(key);
                return cached;
            }

            var wrapped = WrapperTemplate.Replace("{USER_CODE}", Indent(userCode, 12));
            var syntaxTree = CSharpSyntaxTree.ParseText(wrapped, new CSharpParseOptions());

            var references = CollectReferences();

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

        // 建構清楚的錯誤訊息，行號對齊到使用者碼
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

        // 自動收集 MetadataReference：從目前 AppDomain 已載入組件 + 必要錨點
        private static IEnumerable<MetadataReference> CollectReferences()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc))
                        paths.Add(loc);
                }
                catch { /* in-memory 動態組件沒有 Location，忽略 */ }
            }

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
                    if (!string.IsNullOrEmpty(loc)) paths.Add(loc);
                }
                catch { }
            }

            // 少數環境會需要 netstandard，抓不到就忽略
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
                if (!string.IsNullOrEmpty(loc)) set.Add(loc);
            }
            catch { }
        }

        // 可回收的 AssemblyLoadContext
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

        private static void PutIntoCache(string key, byte[] dll)
        {
            _dllCache[key] = dll;
            lock (_lruLock)
            {
                _lruList.Remove(key);
                _lruList.AddFirst(key);
                while (_lruList.Count > CacheCapacity)
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
    }

    // ============================
    // 外部事件處理器：只負責 Transaction + 呼叫引擎
    // ============================
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _generatedCode;
        private object[] _executionParameters;

        public ExecutionResultInfo ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetExecutionParameters(string code, object[] parameters = null)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
            => _resetEvent.WaitOne(timeoutMilliseconds);

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                ResultInfo = new ExecutionResultInfo();

                using (var transaction = new Transaction(doc, "Execute AI code"))
                {
                    transaction.Start();
                    var fho = transaction.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(new DismissAllFailures());
                    transaction.SetFailureHandlingOptions(fho);

                    // 呼叫強壯版引擎（Roslyn + Collectible ALC + 快取）
                    var result = MiniScriptEngine.Run(_generatedCode, doc, _executionParameters);

                    transaction.Commit();

                    ResultInfo.Success = true;
                    ResultInfo.Result = JsonConvert.SerializeObject(result);
                }
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
    // DTO / Failure
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
                if (fma.GetSeverity() == FailureSeverity.Warning)
                    accessor.DeleteWarning(fma);

            return FailureProcessingResult.Continue;
        }
    }
}
