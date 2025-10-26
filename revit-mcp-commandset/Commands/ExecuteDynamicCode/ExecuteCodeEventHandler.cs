using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// 處理代碼執行的外部事件處理器 (Revit 2025 / .NET 8)
    /// </summary>
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        // 代碼執行參數
        private string _generatedCode;
        private object[] _executionParameters;

        // 執行結果信息
        public ExecutionResultInfo ResultInfo { get; private set; }

        // Synchronization primitives
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        // 設置要執行的代碼和參數
        public void SetExecutionParameters(string code, object[] parameters = null)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        // 等待執行完成 - IWaitableExternalEventHandler接口實現
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

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

                    // 動態編譯執行代碼 (使用 Roslyn)
                    var result = CompileAndExecuteCode(
                        code: _generatedCode,
                        doc: doc,
                        parameters: _executionParameters
                    );

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

        /// <summary>
        /// 使用 Roslyn 動態編譯並執行 C# 代碼片段
        /// </summary>
        private object CompileAndExecuteCode(string code, Document doc, object[] parameters)
        {
            // 包裝用戶代碼為靜態方法
            var wrappedCode = $@"
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
                {{
                    public static class CodeExecutor
                    {{
                        public static object Execute(Autodesk.Revit.DB.Document document, object[] parameters)
                        {{
                            // 用戶代碼入口
                            {code}
                        }}
                    }}
                }}";

            // 使用 Roslyn 解析語法樹
            var syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);

            // 收集必要的程序集引用
            var references = new List<MetadataReference>
            {
                // 基礎 .NET 程序集
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
                
                // Revit API 程序集
                MetadataReference.CreateFromFile(typeof(Document).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(UIApplication).Assembly.Location),
                
                // .NET 運行時程序集
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Linq").Location),
                MetadataReference.CreateFromFile(Assembly.Load("netstandard").Location)
            };

            // 創建編譯選項
            var compilation = CSharpCompilation.Create(
                assemblyName: $"DynamicAssembly_{Guid.NewGuid():N}",
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            // 編譯到記憶體流
            using var ms = new MemoryStream();
            EmitResult emitResult = compilation.Emit(ms);

            // 處理編譯錯誤
            if (!emitResult.Success)
            {
                var errors = string.Join("\n", emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d =>
                    {
                        var lineSpan = d.Location.GetLineSpan();
                        return $"Line {lineSpan.StartLinePosition.Line + 1}: {d.GetMessage()}";
                    }));
                throw new Exception($"代碼編譯錯誤:\n{errors}");
            }

            // 載入編譯後的程序集並執行
            ms.Seek(0, SeekOrigin.Begin);
            var assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
            var executorType = assembly.GetType("AIGeneratedCode.CodeExecutor");
            var executeMethod = executorType.GetMethod("Execute");

            return executeMethod.Invoke(null, new object[] { doc, parameters });
        }

        public string GetName() => "AI Dynamic Code Executor";
    }

    // 執行結果數據結構
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
    /// 抑制所有警告 (FailureSeverity.Warning)
    /// 防止模態對話框中斷外部自動化
    /// </summary>
    internal class DismissAllFailures : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            IList<FailureMessageAccessor> failList = accessor.GetFailureMessages();

            foreach (var fma in failList)
            {
                FailureSeverity severity = fma.GetSeverity();

                if (severity == FailureSeverity.Warning)
                    accessor.DeleteWarning(fma);
            }
            return FailureProcessingResult.Continue;
        }
    }
}