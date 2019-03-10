using System;
using System.IO;
using Neo.Core.Extensibility;

namespace Neo.Server
{
    internal class Program
    {
        private static readonly NeoServer server = new NeoServer();
		
		private static readonly String configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"config.json");
		private static readonly String dataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"data");
		private static readonly String pluginPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"plugins");
		

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
