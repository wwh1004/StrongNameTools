using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;

namespace StrongNameBatchRemover {
	internal static class Program {
		private static readonly byte[] NullWithSpacesBytes = Encoding.UTF8.GetBytes("null".PadRight(16));
		private static readonly Dictionary<string, byte[]> FileDatas = new Dictionary<string, byte[]>();
		// TODO: 更新缓存方式,可以放到上下文里面

		private sealed class Context {
			public List<ModuleDefMD> Modules;
			public List<(ModuleDefMD Module, ModuleDefMD[] References)> ReferencesMap;
			public Dictionary<ModuleDefMD, PatchInfo> PatchInfos;
			public HashSet<ModuleDefMD> PendingModules;
			public HashSet<ModuleDefMD> ProcessingModules;
			public HashSet<ModuleDefMD> ProcessedModules;

			public Context(List<ModuleDefMD> modules, List<(ModuleDefMD Module, ModuleDefMD[] References)> referencesMap) {
				Modules = modules;
				ReferencesMap = referencesMap;
				PatchInfos = new Dictionary<ModuleDefMD, PatchInfo>();
				PendingModules = new HashSet<ModuleDefMD>();
				ProcessingModules = new HashSet<ModuleDefMD>();
				ProcessedModules = new HashSet<ModuleDefMD>();
			}
		}

		private static void Main(string[] args) {
			string directory = Path.GetFullPath(args[0]);
			string[] assemblyPaths = args.Skip(1).Select(t => Path.GetFullPath(t)).ToArray();
			Execute(assemblyPaths, directory);
			Console.ReadKey(true);
		}

		private static void Execute(string[] assemblyPaths, string directory) {
			var modules = LoadModules(directory);
			var referencesMap = LoadReferencesMap(modules);
			var context = new Context(modules, referencesMap);
			CreatePatchInfos(assemblyPaths, context);
			foreach (var module in modules)
				module.Dispose();
			ApplyPatchInfos(context.PatchInfos.Select(t => (t.Key.Location, t.Value)));
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

		private static List<(ModuleDefMD Module, ModuleDefMD[] References)> LoadReferencesMap(IEnumerable<ModuleDefMD> modules) {
			Console.WriteLine("开始解析引用");
			var referencesMap = new List<(ModuleDefMD Module, ModuleDefMD[] References)>();
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

				referencesMap.Add((module, resolvedReferences.ToArray()));
			}
			Console.WriteLine("解析引用完成");
			Console.WriteLine();
			return referencesMap;
		}

		private static void CreatePatchInfos(string[] assemblyPaths, Context context) {
			Console.WriteLine("开始创建 PatchInfo");
			context.PendingModules = assemblyPaths.Select(m => context.Modules.FirstOrDefault(n => n.Location == m)).Where(t => t?.IsStrongNameSigned == true).ToHashSet();
			while (context.PendingModules.Count != 0) {
				context.ProcessingModules = context.PendingModules;
				context.PendingModules = new HashSet<ModuleDefMD>();
				//{
				//	Parallel.ForEach(context.ProcessingModules, t => CreatePatchInfos(t, context));
				//	// TODO: 多线程支持
				//}
				foreach (var module in context.ProcessingModules)
					CreatePatchInfos(module, context);
			}
			Console.WriteLine("创建 PatchInfo 完成");
			Console.WriteLine();
		}

		/// <summary>
		/// 创建被引用程序集的强名称和引用方 AssemblyRef 表的patch信息
		/// </summary>
		/// <param name="module">被引用的程序集</param>
		/// <param name="context"></param>
		private static void CreatePatchInfos(ModuleDefMD module, Context context) {
			if (!module.IsStrongNameSigned)
				throw new InvalidOperationException();
			if (!context.ProcessedModules.Add(module))
				throw new InvalidOperationException();

			Console.WriteLine($"PatchInfo: {module}");
			CreateStrongNamePatchInfo(module, context);
			// 获取refee本身的强名称PatchInfo
			foreach (var refer in context.Modules) {
				CreateAssemblyRefPatchInfo(refer, module, context);
				// 获取引用了refee的程序集的AssemblyRef表PatchInfo
				CreateAssemblyNamePatchInfo(refer, module, context);
			}
		}

		/// <summary>
		/// patch掉 <paramref name="module"/> 的强名称信息
		/// </summary>
		/// <param name="module"></param>
		/// <param name="context"></param>
		private static void CreateStrongNamePatchInfo(ModuleDefMD module, Context context) {
			Console.WriteLine($"  PatchInfo (StrongName): {module.Assembly.Name}");
			var patchInfo = new PatchInfo();
			int cor20HeaderOffset = (int)module.Metadata.ImageCor20Header.StartOffset;
			int offset = cor20HeaderOffset + 0x10;
			uint value4 = (uint)(module.Cor20HeaderFlags & ~dnlib.DotNet.MD.ComImageFlags.StrongNameSigned);
			patchInfo.Add(offset, BitConverter.GetBytes(value4));
			// Cor20Header.Flags
			offset = cor20HeaderOffset + 0x20;
			patchInfo.Add(offset, new byte[8]);
			// Cor20Header.StrongNameSignature
			var assemblyTable = module.TablesStream.AssemblyTable;
			int tableOffset = (int)assemblyTable.StartOffset;
			int rowOffset = (int)(module.Assembly.Rid - 1) * (int)assemblyTable.RowSize;
			int flagsColumnOffset = assemblyTable.Columns[5].Offset;
			int publicKeyColumnOffset = assemblyTable.Columns[6].Offset;
			int publicKeyColumnSize = assemblyTable.Columns[6].Size;
			patchInfo.Add(tableOffset + rowOffset + flagsColumnOffset, BitConverter.GetBytes((uint)(module.Assembly.Attributes & ~AssemblyAttributes.PublicKey)));
			patchInfo.Add(tableOffset + rowOffset + publicKeyColumnOffset, new byte[publicKeyColumnSize]);
			UpdatePatchInfo(context.PatchInfos, module, patchInfo);
		}

		/// <summary>
		/// <paramref name="refer"/> 引用了 <paramref name="refee"/> ,<paramref name="refee"/> 是一个没强名称的模块，我们要patch掉 <paramref name="refer"/> 中 AssemblyRef 表的强名称信息
		/// </summary>
		/// <param name="refer"></param>
		/// <param name="refee"></param>
		/// <param name="context"></param>
		private static void CreateAssemblyRefPatchInfo(ModuleDefMD refer, ModuleDefMD refee, Context context) {
			var assemblyRefs = refer.GetAssemblyRefs().Where(t => !t.PublicKeyOrToken.IsNullOrEmpty && (refer.Context.AssemblyResolver.Resolve(t, refer)?.ManifestModule) == refee).ToArray();
			if (assemblyRefs.Length == 0)
				return;

			Console.WriteLine($"  PatchInfo (AssemblyRef): {refer.Assembly.Name} -> {refee.Assembly.Name}");
			var patchInfo = new PatchInfo();
			var assemblyRefTable = refer.TablesStream.AssemblyRefTable;
			int tableOffset = (int)assemblyRefTable.StartOffset;
			int flagsColumnOffset = assemblyRefTable.Columns[4].Offset;
			int publicKeyOrTokenColumnOffset = assemblyRefTable.Columns[5].Offset;
			int publicKeyOrTokenColumnSize = assemblyRefTable.Columns[5].Size;
			foreach (var assemblyRef in assemblyRefs) {
				int rowOffset = (int)(assemblyRef.Rid - 1) * (int)assemblyRefTable.RowSize;
				if (assemblyRef.HasPublicKey)
					patchInfo.Add(tableOffset + rowOffset + flagsColumnOffset, BitConverter.GetBytes((uint)(assemblyRef.Attributes & ~AssemblyAttributes.PublicKey)));
#if DEBUG
				System.Diagnostics.Debug.Assert(assemblyRef.Hash is null || assemblyRef.Hash.Length == 0);
#endif
				patchInfo.Add(tableOffset + rowOffset + publicKeyOrTokenColumnOffset, new byte[publicKeyOrTokenColumnSize]);
			}
			UpdatePatchInfo(context.PatchInfos, refer, patchInfo);
			if (refer.IsStrongNameSigned && !context.ProcessingModules.Contains(refer) && !context.ProcessedModules.Contains(refer))
				context.PendingModules.Add(refer);
		}

		private static void CreateAssemblyNamePatchInfo(ModuleDefMD refer, ModuleDefMD refee, Context context) {
			if (!FileDatas.TryGetValue(refer.Location, out byte[] data)) {
				data = File.ReadAllBytes(refer.Location);
				FileDatas.Add(refer.Location, data);
			}
			int[] offsets = AssemblyNameFinder.FindAll(data, refee.Assembly);
			if (offsets.Length == 0)
				return;

			Console.WriteLine($"  PatchInfo (AssemblyName): {refer.Assembly.Name} -> {refee.Assembly.Name}");
			var patchInfo = new PatchInfo();
			foreach (int offset in offsets)
				patchInfo.Add(offset, NullWithSpacesBytes);
			UpdatePatchInfo(context.PatchInfos, refer, patchInfo);
			if (refer.IsStrongNameSigned && !context.ProcessingModules.Contains(refer) && !context.ProcessedModules.Contains(refer))
				context.PendingModules.Add(refer);
		}

		private static void UpdatePatchInfo(Dictionary<ModuleDefMD, PatchInfo> patchInfos, ModuleDefMD module, PatchInfo patchInfo) {
			// TODO: 多线程支持
			if (patchInfos.TryGetValue(module, out var main))
				MergePatchInfo(main, patchInfo);
			else
				patchInfos.Add(module, patchInfo);
		}

		private static void MergePatchInfo(PatchInfo main, PatchInfo other) {
			foreach (var (key, value) in other) {
				if (main.TryGetValue(key, out byte[] oldValue)) {
					if (!value.AsSpan().SequenceEqual(oldValue))
						throw new InvalidOperationException("不匹配的值");
				}
				else {
					main.Add(key, value);
				}
			}
		}

		private static void ApplyPatchInfos(IEnumerable<(string AssemblyPath, PatchInfo PatchInfo)> patchInfos) {
			Console.WriteLine("开始应用 PatchInfo");
			foreach (var (assemblyPath, patchInfo) in patchInfos) {
				using var file = new FileStream(assemblyPath, FileMode.Open);
				foreach (var (offset, value) in patchInfo) {
					file.Position = offset;
					file.Write(value);
				}
			}
			Console.WriteLine("应用 PatchInfo 完成");
		}
	}
}
