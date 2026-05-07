namespace TinySerializer.Utilities.Misc {
    public interface ICacheNotificationReceiver {
        void OnFreed();
        
        void OnClaimed();
    }
}