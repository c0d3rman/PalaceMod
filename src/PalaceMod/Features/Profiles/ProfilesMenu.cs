namespace PalaceMod.Profiles
{
    internal sealed class ProfilesMenu
    {
        private readonly ProfilesManager manager;
        public ProfilesMenu(ProfilesManager manager) { this.manager = manager; }
        public void Update() { }
        public void Open() { }
        public void Close() { }
        public void Remove() { }
    }
}
