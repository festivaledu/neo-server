using System;
using System.IO;

namespace Neo.Server
{
    internal class Program
    {
        private static readonly NeoServer server = new NeoServer();

        private static readonly string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"config.json");
        private static readonly string dataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"data");
        private static readonly string pluginPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"plugins");

        internal static void Main(string[] args) {
            if (!Directory.Exists(dataPath)) {
                Directory.CreateDirectory(dataPath);
            }
            if (!Directory.Exists(pluginPath)) {
                Directory.CreateDirectory(pluginPath);
            }

            server.Initialize(configPath, dataPath, pluginPath);

            server.Start();

            Console.ReadLine();
            server.Stop();

            Console.ReadLine();
        }
    }
}
