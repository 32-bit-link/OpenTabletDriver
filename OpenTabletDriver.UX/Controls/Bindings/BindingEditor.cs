using System;
using Eto.Forms;
using OpenTabletDriver.Desktop.Profiles;

namespace OpenTabletDriver.UX.Controls.Bindings
{
    public abstract class BindingEditor : Panel
    {
        public DirectBinding<BindingSettings> SettingsBinding => ProfileBinding.Child(b => b!.BindingSettings);

        private Profile? _profile;
        public Profile? Profile
        {
            set
            {
                _profile = value;
                OnProfileChanged();
            }
            get => _profile;
        }

        public event EventHandler<EventArgs>? ProfileChanged;

        protected virtual void OnProfileChanged() => ProfileChanged?.Invoke(this, EventArgs.Empty);

        public BindableBinding<BindingEditor, Profile?> ProfileBinding
        {
            get
            {
                return new BindableBinding<BindingEditor, Profile?>(
                    this,
                    c => c.Profile,
                    (c, v) => c.Profile = v,
                    (c, h) => c.ProfileChanged += h,
                    (c, h) => c.ProfileChanged -= h
                );
            }
        }
    }
}
