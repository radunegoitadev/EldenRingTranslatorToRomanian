using System.Xml.Linq;
using System.Text;
using System.Text.Json;
using DotNetEnv;

namespace EldenRingTranslatorToRomanian
{
    internal class Program
    {
        private static readonly HttpClient Client = new() {Timeout = TimeSpan.FromMinutes(5)};
        private static int _errors;
        private static readonly string? EndPoint;
        private static readonly string? RootPath;

        static Program()
        {
            Env.TraversePath().Load("LocalEnv.env");
            EndPoint = Environment.GetEnvironmentVariable("ENDPOINT");
            RootPath = Environment.GetEnvironmentVariable("ROOTPATH");
        }

        private static async Task Main()
        {
            if (RootPath != null)
            {
                var allDcxFiles = Directory.GetDirectories(RootPath,"*-dcx");
                _errors = 0;

                Console.WriteLine($"\nFound {allDcxFiles.Length} folders \n");

                foreach(var doc in allDcxFiles)
                {
                    var xmlFiles = Directory.GetFiles(doc,"*.fmg.xml");
                    Console.WriteLine($"Found {xmlFiles.Length} XML files in Folder {doc} \n");

                    foreach(var file in xmlFiles)
                    {
                        var document = XDocument.Load(file);
                        Console.WriteLine($"\nTranslating the file {Path.GetFileName(file)}\n");
                        var progressFile = file + ".progress.txt";
                        HashSet<string> processedIds = [];

                        if (File.Exists(progressFile))
                        {
                            processedIds = [.. await File.ReadAllLinesAsync(progressFile)];
                            Console.ForegroundColor = ConsoleColor.DarkGreen;
                            Console.WriteLine($"[Save] Found {processedIds.Count} already translated lines\n");
                            Console.ResetColor();
                        }
                    
                        var nodes = document.Descendants("text").ToList();
        
                        var batch = new Dictionary<string, string>();
                        const int batchSize = 50;

                        if (Path.GetFileName(file) == "ToS_win64.fmg.xml") continue;

                        for (var i = 0; i < nodes.Count; i++)
                        {
                            var id = nodes[i].Attribute("id")?.Value;
                            var text = nodes[i].Value;

                            if(string.IsNullOrWhiteSpace(id) || processedIds.Contains(id.Trim()))
                            {
                                continue;
                            }

                            if (!string.IsNullOrWhiteSpace(text) && text != "%null%" && text != "[ERROR]" && text != "(dummyText)") 
                            {
                                batch.Add(id, text);
                            }

                            if (batch.Count != batchSize && (i != nodes.Count - 1 || batch.Count <= 0)) continue;
                            Console.WriteLine($"Translating a batch of {batch.Count} lines...");
                            var translatedBatch = await Translate(batch);

                            if(translatedBatch != null)
                            {
                                foreach (var kvp in translatedBatch)
                                {
                                    if(kvp.Key == null || kvp.Value == null) continue;

                                    var nodeToUpdate = nodes.FirstOrDefault(n => n.Attribute("id")?.Value == kvp.Key.Trim());

                                    if (nodeToUpdate == null) continue;

                                    if(!string.IsNullOrWhiteSpace(kvp.Value))
                                    {
                                        nodeToUpdate.Value = kvp.Value.Replace("[EROARE]", "[ERROR]").Replace("ă","a").Replace("Ă","A").Replace("î","i").Replace("Î","I").Replace("ș","s").Replace("Ș","S").Replace("ț","t").Replace("Ț","T").Replace("â", "a").Replace("Â", "A");
                                    }
                                    else
                                    {
                                        _errors++;
                                        Console.ForegroundColor = ConsoleColor.Yellow;
                                        Console.WriteLine($"[Warning] The AI returned a invalid id ({kvp.Key}). I skipped it.");
                                        Console.ResetColor();
                                    }
                                }
                            }
                            batch.Clear();
                            document.Save(file);

                            if(translatedBatch != null)
                            {
                                await File.AppendAllLinesAsync(progressFile,translatedBatch.Keys);
                            }

                            await Task.Delay(15000);
                        }
                    }
                }
            
                if(_errors == 0)
                {
                    foreach(var folders in allDcxFiles)
                    {
                        var saveFiles = Directory.GetFiles(folders,"*.progress.txt");

                        foreach(var saveFile in saveFiles)
                        {
                            File.Delete(saveFile);
                        }
                    }

                    Console.ForegroundColor = ConsoleColor.DarkBlue;
                    Console.WriteLine("\n[Info] Cleaned all Progress Files");
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine("\n[Status] Completed Translation Process\n");
                    Console.ResetColor();
                }
            }
        }

        private static async Task<Dictionary<string,string>?> Translate(Dictionary<string,string> inputBatch)
        {
            var jsonInput = JsonSerializer.Serialize(inputBatch);
        
            const string prompt = "Esti un traducator expert de jocuri dark-fantasy. Tradu valorile din acest JSON in romana. " + "Pastreaza structura XML (ex: <?keyicon?>). " + "Returneaza STRICT un obiect JSON valid cu aceleasi chei si valorile traduse. Fara comentarii suplimentare." + "Toate cheile (inclusiv ID-urile numerice, de exemplu \"2120201\") si valorile trebuie sa fie incadrate OBLIGATORIU in ghilimele duble." + " IMPORTANT: Orice ID trebuie sa aiba ghilimele duble! Exemplu corect: \"212752\": \"Text\". Exemplu GREȘIT: 212752: \"Text\" " + "PASTREAZA EXACT ASA CUM SUNT variabilele de cod (precum <?remainingSec?>, <?placeName?>) si textele \"%null%\". Nu le traduce si asigura-te ca le pui in interiorul ghilimelelor ca text normal. (Exemplu: \"212000\": \"Cheltuie <?demandSoul?> rune si creste in nivel?\").";

            var request = new
            {
                systemInstruction = new { parts = new[] { new { text = prompt } } },
                contents = new[] { new { parts = new[] { new { text = jsonInput } } } },
                generationConfig = new { temperature = 0.1, responseMimeType = "application/json" }
            };

            var jsonBody = JsonSerializer.Serialize(request);
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            try
            {
                var response = await Client.PostAsync(EndPoint, content);
                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                
                var rawOutput = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                
                if (string.IsNullOrWhiteSpace(rawOutput))
                {
                    return new Dictionary<string,string>();
                }

                rawOutput = rawOutput.Replace("```json", "").Replace("```", "").Trim();

                return JsonSerializer.Deserialize<Dictionary<string, string>>(rawOutput) ?? new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                _errors++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] Something went wrong at this batch: {ex.Message}");
                Console.ResetColor();

                if (!ex.Message.Contains("429")) return null;

                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine("\n[Status] API Limit Reached.. Exiting App\n");
                Console.ResetColor();
                Environment.Exit(0);

                return null;
            }
        }
    }
}