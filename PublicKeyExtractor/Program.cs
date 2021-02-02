using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;

namespace PublicKeyExtractor {
	internal static class Program {
		private static void Main(string[] args) {
			string directory = Path.GetFullPath(args[0]);
			string[] assemblyPaths = args.Skip(1).Select(t => Path.GetFullPath(t)).ToArray();
			Execute(assemblyPaths, directory);
			Console.ReadKey(true);
		}

		private static void Execute(string[] assemblyPaths, string directory) {
			foreach (string filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(t => Path.GetFullPath(t))) {
				if (filePath.Contains(@"_References\"))
					continue;
				if (!filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
					continue;
				if (assemblyPaths.Length != 0 && !assemblyPaths.Contains(filePath))
					continue;

				ModuleDefMD module = null;
				try {
					module = ModuleDefMD.Load(filePath, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
				}
				catch {
					module?.Dispose();
					continue;
				}

				if (!module.Assembly.HasPublicKey)
					continue;

				var snk = new StrongNamePublicKey(module.Assembly.PublicKey);
				byte[] data = snk.CreatePublicKey();
				string fileName = Path.GetFileName(filePath);
				string snkDirectory = Path.Combine(directory, "snks", Path.GetFileNameWithoutExtension(fileName));
				if (!Directory.Exists(snkDirectory))
					Directory.CreateDirectory(snkDirectory);
				string snkFilePath = Path.Combine(snkDirectory, Path.ChangeExtension(fileName, ".snk"));
				File.WriteAllBytes(snkFilePath, data);
				Console.WriteLine(snkFilePath);
				module.Dispose();
			}
		}
	}
}
