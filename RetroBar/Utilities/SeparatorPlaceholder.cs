namespace RetroBar.Utilities
{
    internal sealed class SeparatorPlaceholder
    {
        public const string SentinelId = "__tray_separator__";
        public static readonly SeparatorPlaceholder Instance = new();
        private SeparatorPlaceholder() { }
    }
}
