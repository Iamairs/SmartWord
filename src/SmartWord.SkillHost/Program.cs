using System;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using SmartWord.Core.Models;
using SmartWord.OfficeIntegration.SkillScripts;

namespace SmartWord.SkillHost
{
    internal static class Program
    {
        private static int Main()
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                var requestJson = Console.In.ReadLine();
                var request = JsonConvert.DeserializeObject<SkillScriptRunRequest>(requestJson ?? string.Empty);
                if (request == null)
                {
                    Console.Error.WriteLine("SkillHost 请求为空或格式无效。");
                    return 2;
                }

                var runner = new SkillScriptRunner();
                var result = runner.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
                Console.Out.Write(JsonConvert.SerializeObject(
                    result,
                    Formatting.None,
                    new JsonSerializerSettings
                    {
                        StringEscapeHandling = StringEscapeHandling.EscapeNonAscii
                    }));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }
    }
}
