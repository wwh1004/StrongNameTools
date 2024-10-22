using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;

namespace StrongNameFinder {
	internal static class Program {
		private sealed class Context {
			public List<ModuleDefMD> Modules;
			public Dictionary<ModuleDefMD, ModuleDefMD[]> XrefsFromMap;
			public HashSet<ModuleDefMD> PendingModules;
			public HashSet<ModuleDefMD> ProcessingModules;
			public HashSet<ModuleDefMD> ProcessedModules;

			public Context(List<ModuleDefMD> modules, Dictionary<ModuleDefMD, ModuleDefMD[]> xrefsFromMap) {
				Modules = modules;
				XrefsFromMap = xrefsFromMap;
				PendingModules = new HashSet<ModuleDefMD>();
				ProcessingModules = new HashSet<ModuleDefMD>();
				ProcessedModules = new HashSet<ModuleDefMD>();
			}
		}

		private static void Main(string[] args) {
			string directory = Path.GetFullPath(args[0]);
			Execute(directory);
			Console.ReadKey(true);
		}

		private static void Execute(string directory) {
			var modules = LoadModules(directory);
			var xrefsFromMap = LoadXrefsFromMap(LoadXrefsToMap(modules));
			var context = new Context(modules, xrefsFromMap) {
				PendingModules = modules.Where(t => t.IsStrongNameSigned).ToHashSet()
			};
			int indent = 0;
			while (context.PendingModules.Count != 0) {
				context.ProcessingModules = context.PendingModules;
				context.PendingModules = new HashSet<ModuleDefMD>();
				foreach (var module in context.ProcessingModules) {
					Console.WriteLine(new string(' ', indent) + Path.GetRelativePath(directory, module.Location));
					foreach (var xref in context.XrefsFromMap[module]) {
						if (!context.ProcessingModules.Contains(xref) && !context.ProcessedModules.Contains(xref))
							context.PendingModules.Add(xref);
					}
					if (!context.ProcessedModules.Add(module))
						throw new InvalidOperationException();
				}
				indent += 2;
			}
			Console.WriteLine();
			foreach (var strongNameModule in modules.Where(t => t.IsStrongNameSigned)) {
				context = new Context(modules, xrefsFromMap) {
					PendingModules = new HashSet<ModuleDefMD> { strongNameModule }
				};
				indent = 0;
				while (context.PendingModules.Count != 0) {
					context.ProcessingModules = context.PendingModules;
					context.PendingModules = new HashSet<ModuleDefMD>();
					foreach (var module in context.ProcessingModules) {
						Console.WriteLine(new string(' ', indent) + Path.GetRelativePath(directory, module.Location));
						foreach (var xref in context.XrefsFromMap[module]) {
							if (!context.ProcessingModules.Contains(xref) && !context.ProcessedModules.Contains(xref))
								context.PendingModules.Add(xref);
						}
						if (!context.ProcessedModules.Add(module))
							throw new InvalidOperationException();
					}
					indent += 2;
				}
				Console.WriteLine();
			}
			foreach (var module in modules)
				module.Dispose();
		}

		private static List<ModuleDefMD> LoadModules(string directory) {
			Console.WriteLine("开始加载模块");
			var modules = new List<ModuleDefMD>();
			foreach (string filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(t => Path.GetFullPath(t))) {
				if (filePath.Contains(@"_References\"))
					continue;
				if (!filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
					continue;

				ModuleDefMD module = null;
				try {
					module = ModuleDefMD.Load(filePath, new ModuleCreationOptions { TryToLoadPdbFromDisk = false });
				}
				catch {
					module?.Dispose();
					continue;
				}
				modules.Add(module);
			}

			var hashSet1 = new HashSet<string>();
			var hashSet2 = new HashSet<string>();
			foreach (var module in modules) {
				string assemblyName = module.Assembly.Name;
				if (!hashSet1.Add(assemblyName))
					hashSet2.Add(assemblyName);
			}
			if (hashSet2.Count != 0) {
				Console.WriteLine("存在同名程序集：");
				foreach (string assemblyName in hashSet2)
					Console.WriteLine(assemblyName);
				throw new NotSupportedException("存在同名程序集");
			}

			var moduleContext = CreateModuleContext(modules);
			foreach (var module in modules)
				module.Context = moduleContext;

			Console.WriteLine("加载模块完成");
			Console.WriteLine();
			return modules;
		}

		private static ModuleContext CreateModuleContext(IEnumerable<ModuleDef> modules) {
			var moduleContext = new ModuleContext();
			var assemblyResolver = new IsolatedAssemblyResolver(moduleContext) { UseGAC = false };
			var assemblies = modules.Select(t => t.Assembly).ToArray();
			if (!assemblyResolver.AddToCache(assemblies))
				throw new InvalidOperationException();
			var resolver = new Resolver(assemblyResolver);
			moduleContext.AssemblyResolver = assemblyResolver;
			moduleContext.Resolver = resolver;
			assemblyResolver.DefaultModuleContext = moduleContext;
			return moduleContext;
		}

		private static Dictionary<ModuleDefMD, ModuleDefMD[]> LoadXrefsToMap(IEnumerable<ModuleDefMD> modules) {
			Console.WriteLine("开始解析引用");
			var xrefsToMap = new Dictionary<ModuleDefMD, ModuleDefMD[]>();
			foreach (var module in modules) {
				Console.WriteLine($"正在解析 {module.Assembly.Name} 的引用");
				var assemblyRefs = module.GetAssemblyRefs().Distinct(AssemblyNameComparer.CompareAll).Cast<AssemblyRef>().ToArray();
				var resolvedReferences = new List<ModuleDefMD>();
				var unresolvedReferences = new List<AssemblyRef>();
				foreach (var assemblyRef in assemblyRefs) {
					var resolvedReference = module.Context.AssemblyResolver.Resolve(assemblyRef, module);
					if (!(resolvedReference is null))
						resolvedReferences.Add((ModuleDefMD)resolvedReference.ManifestModule);
					else
						unresolvedReferences.Add(assemblyRef);
				}

				var gacAssemblyResolver = new IsolatedAssemblyResolver();
				unresolvedReferences = unresolvedReferences.Where(t => gacAssemblyResolver.Resolve(t, module) is null).ToList();
				gacAssemblyResolver.Clear();
				if (unresolvedReferences.Count != 0) {
					Console.WriteLine($"{unresolvedReferences.Count} 个未解析的引用");
					foreach (var unresolvedReference in unresolvedReferences)
						Console.WriteLine(unresolvedReference.ToString());
				}

				xrefsToMap.Add(module, resolvedReferences.ToArray());
			}
			Console.WriteLine("解析引用完成");
			Console.WriteLine();
			return xrefsToMap;
		}

		private static Dictionary<ModuleDefMD, ModuleDefMD[]> LoadXrefsFromMap(Dictionary<ModuleDefMD, ModuleDefMD[]> xrefsToMap) {
			var xrefsFromMap = new Dictionary<ModuleDefMD, ModuleDefMD[]>();
			foreach (var module in xrefsToMap.Keys) {
				var xrefsFrom = new List<ModuleDefMD>();
				foreach (var xrefsTo in xrefsToMap) {
					if (xrefsTo.Value.Contains(module))
						xrefsFrom.Add(xrefsTo.Key);
				}
				xrefsFromMap.Add(module, xrefsFrom.ToArray());
			}
			return xrefsFromMap;
		}
	}
}
