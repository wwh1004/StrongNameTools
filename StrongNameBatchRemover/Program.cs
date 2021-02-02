using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using dnlib.DotNet;

namespace StrongNameBatchRemover {
	internal static class Program {
		private static readonly byte[] NullWithSpacesBytes = Encoding.UTF8.GetBytes("null".PadRight(16));

		private sealed class Context {
			public List<ModuleDefMD> Modules;
			public Dictionary<ModuleDefMD, ModuleDefMD[]> XrefsFromMap;
			public ConcurrentDictionary<ModuleDefMD, PatchInfo> PatchInfos;
			public ConcurrentDictionary<ModuleDefMD, bool> PendingModules;
			public ConcurrentDictionary<ModuleDefMD, bool> ProcessingModules;
			public ConcurrentDictionary<ModuleDefMD, bool> ProcessedModules;
			public ConcurrentDictionary<ModuleDefMD, byte[]> ModuleDatas;
			public ThreadSafeLogger Logger;

			public Context(List<ModuleDefMD> modules, Dictionary<ModuleDefMD, ModuleDefMD[]> xrefsFromMap) {
				Modules = modules;
				XrefsFromMap = xrefsFromMap;
				PatchInfos = new ConcurrentDictionary<ModuleDefMD, PatchInfo>();
				PendingModules = new ConcurrentDictionary<ModuleDefMD, bool>();
				ProcessingModules = new ConcurrentDictionary<ModuleDefMD, bool>();
				ProcessedModules = new ConcurrentDictionary<ModuleDefMD, bool>();
				ModuleDatas = new ConcurrentDictionary<ModuleDefMD, byte[]>();
				Logger = new ThreadSafeLogger();
			}
		}

		private sealed class ThreadSafeLogger {
			private static readonly List<string> PlaceHolder = new List<string>();
			private readonly ConcurrentDictionary<ModuleDefMD, List<string>> _logs = new ConcurrentDictionary<ModuleDefMD, List<string>>();

			public ThreadSafeLogger BeginLog(ModuleDefMD module) {
				return _logs.TryAdd(module, new List<string>()) ? this : throw new InvalidOperationException();
			}

			public ThreadSafeLogger Log(ModuleDefMD module, string text) {
				_logs[module].Add(text);
				return this;
			}

			public void EndLog(ModuleDefMD module) {
				var log = _logs[module];
				Console.WriteLine(string.Join(Environment.NewLine, log));
				_logs[module] = PlaceHolder;
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
			if (assemblyPaths.Length == 0)
				assemblyPaths = modules.Select(t => t.Location).ToArray();
			var xrefsFromMap = LoadXrefsFromMap(LoadXrefsToMap(modules));
			var context = new Context(modules, xrefsFromMap);
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

		private static void CreatePatchInfos(string[] assemblyPaths, Context context) {
			Console.WriteLine("开始创建 PatchInfo");
			context.PendingModules = new ConcurrentDictionary<ModuleDefMD, bool>(assemblyPaths.Select(m => context.Modules.FirstOrDefault(n => n.Location == m)).Where(t => t?.IsStrongNameSigned == true).Select(t => new KeyValuePair<ModuleDefMD, bool>(t, true)));
			while (context.PendingModules.Count != 0) {
				context.ProcessingModules = context.PendingModules;
				context.PendingModules = new ConcurrentDictionary<ModuleDefMD, bool>();
				//foreach (var module in context.ProcessingModules.Keys)
				//	CreatePatchInfos(module, context);
				Parallel.ForEach(context.ProcessingModules.Keys, t => CreatePatchInfos(t, context));
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
			if (!context.ProcessedModules.TryAdd(module, true))
				throw new InvalidOperationException();

			context.Logger.BeginLog(module).Log(module, $"PatchInfo: {module.Assembly.Name}");
			CreateStrongNamePatchInfo(module, context);
			// 获取refee本身的强名称PatchInfo
			foreach (var refer in context.Modules) {
				CreateAssemblyRefPatchInfo(module, refer, context);
				// 获取引用了refee的程序集的AssemblyRef表PatchInfo
				CreateAssemblyNamePatchInfo(module, refer, context);
				// 获取CustomAttribute，Resource等存在AssemblyName位置的PatchInfo，这个不能靠XrefTo得到，有可能存在引用，但是AssemblyRef表不显示
			}
			context.Logger.Log(module, string.Empty).EndLog(module);
		}

		/// <summary>
		/// patch掉 <paramref name="module"/> 的强名称信息
		/// </summary>
		/// <param name="module"></param>
		/// <param name="context"></param>
		private static void CreateStrongNamePatchInfo(ModuleDefMD module, Context context) {
			context.Logger.Log(module, $"  PatchInfo (StrongName): {module.Assembly.Name}");
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
		/// <paramref name="xrefFrom"/> 引用了 <paramref name="module"/> ,<paramref name="module"/> 是一个没强名称的模块，我们要patch掉 <paramref name="xrefFrom"/> 中 AssemblyRef 表的强名称信息
		/// </summary>
		/// <param name="module"></param>
		/// <param name="xrefFrom"></param>
		/// <param name="context"></param>
		private static void CreateAssemblyRefPatchInfo(ModuleDefMD module, ModuleDefMD xrefFrom, Context context) {
			var assemblyRefs = xrefFrom.GetAssemblyRefs().Where(t => !t.PublicKeyOrToken.IsNullOrEmpty && (xrefFrom.Context.AssemblyResolver.Resolve(t, xrefFrom)?.ManifestModule) == module).ToArray();
			if (assemblyRefs.Length == 0)
				return;

			context.Logger.Log(module, $"  PatchInfo (AssemblyRef): {module.Assembly.Name} <- {xrefFrom.Assembly.Name}");
			var patchInfo = new PatchInfo();
			var assemblyRefTable = xrefFrom.TablesStream.AssemblyRefTable;
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
			UpdatePatchInfo(context.PatchInfos, xrefFrom, patchInfo);
			if (xrefFrom.IsStrongNameSigned && !context.ProcessingModules.ContainsKey(xrefFrom) && !context.ProcessedModules.ContainsKey(xrefFrom))
				context.PendingModules.TryAdd(xrefFrom, true);
		}

		/// <summary>
		/// CustomAttribute，Resource等存在AssemblyName位置的PatchInfo
		/// </summary>
		/// <param name="module"></param>
		/// <param name="xrefFrom"></param>
		/// <param name="context"></param>
		private static void CreateAssemblyNamePatchInfo(ModuleDefMD module, ModuleDefMD xrefFrom, Context context) {
			byte[] data = context.ModuleDatas.GetOrAdd(xrefFrom, t => File.ReadAllBytes(t.Location));
			int[] offsets = AssemblyNameFinder.FindAll(data, module.Assembly);
			if (offsets.Length == 0)
				return;

			context.Logger.Log(module, $"  PatchInfo (AssemblyName): {module.Assembly.Name} <- {xrefFrom.Assembly.Name}");
			var patchInfo = new PatchInfo();
			foreach (int offset in offsets)
				patchInfo.Add(offset, NullWithSpacesBytes);
			UpdatePatchInfo(context.PatchInfos, xrefFrom, patchInfo);
			if (xrefFrom.IsStrongNameSigned && !context.ProcessingModules.ContainsKey(xrefFrom) && !context.ProcessedModules.ContainsKey(xrefFrom))
				context.PendingModules.TryAdd(xrefFrom, true);
		}

		private static void UpdatePatchInfo(ConcurrentDictionary<ModuleDefMD, PatchInfo> patchInfos, ModuleDefMD module, PatchInfo patchInfo) {
			patchInfos.AddOrUpdate(module, patchInfo, (_, main) => MergePatchInfo(main, patchInfo));
		}

		private static PatchInfo MergePatchInfo(PatchInfo main, PatchInfo other) {
			foreach (var (key, value) in other) {
				if (main.TryGetValue(key, out byte[] oldValue)) {
					if (!value.AsSpan().SequenceEqual(oldValue))
						throw new InvalidOperationException("不匹配的值");
				}
				else {
					main.Add(key, value);
				}
			}
			return main;
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
