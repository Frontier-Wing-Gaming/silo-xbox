using System;
using System.Linq;
using Windows.Security.Credentials;
using Windows.Storage;

namespace Silolux.Services
{
    /// <summary>
    /// Server address, device identity and the active profile live in local
    /// settings; the refresh token lives in the Credential Locker.
    /// </summary>
    public static class AppSettings
    {
        const string VaultResource = "Silolux";
        static ApplicationDataContainer Local => ApplicationData.Current.LocalSettings;

        static string Read(string key) => Local.Values[key] as string;

        static void Write(string key, string value)
        {
            if (value == null) Local.Values.Remove(key);
            else Local.Values[key] = value;
        }

        public static string ServerUrl
        {
            get => Read("server_url");
            set => Write("server_url", value);
        }

        public static string DeviceId
        {
            get
            {
                var id = Read("device_id");
                if (string.IsNullOrEmpty(id))
                {
                    id = "silolux-xbox-" + Guid.NewGuid().ToString("D");
                    Write("device_id", id);
                }
                return id;
            }
        }

        public static string ProfileId
        {
            get => Read("profile_id");
            set => Write("profile_id", value);
        }

        /// <summary>A PIN profile's verification token. Kept for the session only.</summary>
        public static string ProfileToken { get; set; }

        public static string AccountId
        {
            get => Read("account_id");
            set => Write("account_id", value);
        }

        public static string RefreshToken
        {
            get
            {
                try
                {
                    var vault = new PasswordVault();
                    var cred = vault.FindAllByResource(VaultResource).FirstOrDefault();
                    if (cred == null) return null;
                    cred.RetrievePassword();
                    return cred.Password;
                }
                catch (Exception)
                {
                    // FindAllByResource throws when nothing is stored.
                    return null;
                }
            }
            set
            {
                var vault = new PasswordVault();
                try
                {
                    foreach (var c in vault.FindAllByResource(VaultResource)) vault.Remove(c);
                }
                catch (Exception) { }
                if (!string.IsNullOrEmpty(value))
                    vault.Add(new PasswordCredential(VaultResource, "refresh_token", value));
            }
        }

        public static void SignOut()
        {
            RefreshToken = null;
            AccountId = null;
            ProfileId = null;
            ProfileToken = null;
        }
    }
}
