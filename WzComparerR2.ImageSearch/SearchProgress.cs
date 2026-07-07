namespace WzComparerR2.ImageSearch
{
    internal sealed class SearchProgress
    {
        public int ImagesScanned { get; set; }

        public int SpinesScanned { get; set; }

        public int CandidatesCompared { get; set; }

        public int ResultsFound { get; set; }

        public string CurrentPath { get; set; }
    }
}
