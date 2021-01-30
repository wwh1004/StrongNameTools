using System.Collections.Generic;

namespace StrongNameBatchRemover {
	internal sealed class PatchInfo : SortedList<int, byte[]> {
		public PatchInfo() {
		}

		public PatchInfo(int capacity) : base(capacity) {
		}

		public PatchInfo(IDictionary<int, byte[]> dictionary) : base(dictionary) {
		}
	}
}
