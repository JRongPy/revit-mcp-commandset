using System.CodeDom.Compiler;
using Autodesk.Revit.UI;
using Microsoft.CSharp;
using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// 处理代码执行的外部事件处理器
    /// </summary>
    public class ExecuteCodeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        // 代码执行参数
        private string _generatedCode;
        private object[] _executionParameters;

        // 执行结果信息
        public ExecutionResultInfo ResultInfo { get; private set; }

        // Synchronization primitives
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        // 设置要执行的代码和参数
        public void SetExecutionParameters(string code, object[] parameters = null)
        {
            _generatedCode = code;
            _executionParameters = parameters ?? Array.Empty<object>();
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        // 等待执行完成 - IWaitableExternalEventHandler接口实现
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

                    // 动态编译执行代码
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
                ResultInfo.ErrorMessage = $"执行失败: {ex.Message}";
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        /// <summary>
        /// Dynamically compiles and executes a C# code snippet in memory.
        /// </summary>
        private object CompileAndExecuteCode(string code, Document doc, object[] parameters)
        {
            // 添加必要的程序集引用
            var compilerParams = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                CompilerOptions = "/langversion:Default",
                ReferencedAssemblies =
                {
                    "System.dll",
                    "System.Core.dll", 
                    "System.Linq.dll", // For LINQ 
                    "System.Collections.dll",
                    "System.IO.dll",
                    typeof(Document).Assembly.Location,  // RevitAPI.dll
                    typeof(UIApplication).Assembly.Location // RevitAPIUI.dll
                }
            };

            // Wrap user code into a static method entrypoint
            var wrappedCode = $@"
                using System;
                using System.Linq;
                using System.IO;
                using Autodesk.Revit.DB;
                using Autodesk.Revit.UI;
                using System.Collections.Generic;
                using Autodesk.Revit.DB.Structure;
                using Autodesk.Revit.DB.Plumbing;
                using Autodesk.Revit.DB.Mechanical;
                using Autodesk.Revit.DB.Electrical;

                namespace AIGeneratedCode
                {{
                    public static class CodeExecutor
                    {{
                        public static object Execute(Document document, object[] parameters)
                        {{
                            // 用户代码入口
                            {code}
                        }}
                    }}
                }}";

            // 编译代码
            using (var provider = new CSharpCodeProvider())
            {
                var compileResults = provider.CompileAssemblyFromSource(
                    compilerParams,
                    wrappedCode
                );

                // Handle compilation errors
                if (compileResults.Errors.HasErrors)
                {
                    var errors = string.Join("\n", compileResults.Errors
                        .Cast<CompilerError>()
                        .Select(e => $"Line {e.Line}: {e.ErrorText}"));
                    throw new Exception($"代码编译错误:\n{errors}");
                }

                // Execute compiled method via reflection
                var assembly = compileResults.CompiledAssembly;
                var executorType = assembly.GetType("AIGeneratedCode.CodeExecutor");
                var executeMethod = executorType.GetMethod("Execute");

                return executeMethod.Invoke(null, new object[] { doc, parameters });
            }
        }
        public string GetName() => "AI Dynamic Code Executor";
    }

    // 执行结果数据结构
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
    /// Suppresses all warnings (FailureSeverity.Warning) during Revit transactions.
    /// Prevents modal dialogs from interrupting external automation.
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
