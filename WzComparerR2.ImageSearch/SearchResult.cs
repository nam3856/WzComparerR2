using WzComparerR2.WzLib;

namespace WzComparerR2.ImageSearch
{
    internal sealed class SearchResult
    {
        public Wz_Node Node { get; set; }

        public Wz_Node ImageOwnerNode { get; set; }

        public string RelativePath { get; set; }

        public string Kind { get; set; }

        public string Detail { get; set; }

        public string FullPath { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int SizeDistance { get; set; }

        public double Score { get; set; }
    }
}
