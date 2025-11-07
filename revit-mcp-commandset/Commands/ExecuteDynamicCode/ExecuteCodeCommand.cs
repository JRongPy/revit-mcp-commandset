using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using System;

namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// 處理 send_code_to_revit 的命令
    /// </summary>
    public class ExecuteCodeCommand : ExternalEventCommandBase
    {
        private ExecuteCodeEventHandler _handler => (ExecuteCodeEventHandler)Handler;

        public override string CommandName => "send_code_to_revit";

        public ExecuteCodeCommand(UIApplication uiApp)
            : base(new ExecuteCodeEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                // 必填：code
                if (!parameters.ContainsKey("code"))
                    throw new ArgumentException("Missing required parameter: 'code'");

                string code = parameters.Value<string>("code") ?? string.Empty;

                // 可選：parameters
                var parametersArray = parameters["parameters"] as JArray;
                object[] executionParameters = parametersArray?.ToObject<object[]>() ?? Array.Empty<object>();

                // ★ 新增：classes（可為 null / 空字串）
                string classes = parameters.Value<string>("classes");

                // ★ 新增：autoTransaction（預設 true）
                bool autoTx = parameters.Value<bool?>("autoTransaction") ?? true;

                // ★ 新增：transactionName（預設值）
                string txName = parameters.Value<string>("transactionName");
                if (string.IsNullOrWhiteSpace(txName))
                    txName = "Execute AI code";

                // 將參數傳給外部事件處理器（注意：把 classes 一起傳進去）
                _handler.SetExecutionParameters(
                    code: code,
                    parameters: executionParameters,
                    autoTransaction: autoTx,
                    transactionName: txName,
                    classes: classes
                );

                // 觸發外部事件並等待完成
                if (RaiseAndWaitForCompletion(60000)) // 1 分鐘 timeout
                {
                    return _handler.ResultInfo;
                }
                else
                {
                    throw new TimeoutException("代碼執行逾時");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"執行代碼失敗: {ex.Message}", ex);
            }
        }
    }
}
