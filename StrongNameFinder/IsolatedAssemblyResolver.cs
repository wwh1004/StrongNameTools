using System.Collections.Generic;
using dnlib.DotNet;

namespace StrongNameFinder {
	internal sealed class IsolatedAssemblyResolver : AssemblyResolver {
		private static readonly string[] _emptyPaths = new string[0];

		public IsolatedAssemblyResolver() : base() {
		}

		public IsolatedAssemblyResolver(ModuleContext defaultModuleContext) : base(defaultModuleContext) {
		}

		public bool AddToCache(IEnumerable<ModuleDef> modules) {
			bool result = true;
			foreach (var module in modules)
				result &= AddToCache(module);
			return result;
		}

		public bool AddToCache(IEnumerable<AssemblyDef> assemblies) {
			bool result = true;
			foreach (var assembly in assemblies)
				result &= AddToCache(assembly);
			return result;
		}

		protected override IEnumerable<string> PreFindAssemblies(IAssembly assembly, ModuleDef sourceModule, bool matchExactly) {
			return _emptyPaths;
		}

		protected override IEnumerable<string> PostFindAssemblies(IAssembly assembly, ModuleDef sourceModule, bool matchExactly) {
			return _emptyPaths;
		}

		protected override IEnumerable<string> GetModuleSearchPaths(ModuleDef module) {
			return _emptyPaths;
		}
	}
}
