using System;
using System.IO;
using Neo.Core.Networking;
using Neo.Core.Shared;

namespace Neo.Server
{
    internal class Program
    {
        private static readonly NeoServer server = new NeoServer();

        private static readonly string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"config.json");
        private static readonly string dataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"data");
        private static readonly string pluginPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"plugins");
        private static readonly string avatarPath = Path.Combine(dataPath, @"avatars");

        internal static void Main(string[] args) {
            if (!Directory.Exists(dataPath)) {
                Directory.CreateDirectory(dataPath);
            }

            if (!Directory.Exists(pluginPath)) {
                Directory.CreateDirectory(pluginPath);
            }
            
            if (!Directory.Exists(avatarPath)) {
                Directory.CreateDirectory(avatarPath);
            }

            server.Initialize(configPath, dataPath, pluginPath);
            server.Start();

            var webServer = new HttpServer(avatarPath, 43430);

            Console.ReadLine();

            server.Stop();
            webServer.Stop();

            Logger.Instance.Log(LogLevel.Ok, "You may now exit the program.", true);
        }
    }
}
