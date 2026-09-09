namespace SistemaNacoMac.Services;

public sealed class AppSettings
{
    public string Server { get => Preferences.Default.Get(nameof(Server), "CA01DB01"); set => Preferences.Default.Set(nameof(Server), value); }
    public string Database { get => Preferences.Default.Get(nameof(Database), "SISTEMA_NACO"); set => Preferences.Default.Set(nameof(Database), value); }
    public string User { get => Preferences.Default.Get(nameof(User), "naco_app"); set => Preferences.Default.Set(nameof(User), value); }
    public string Password { get => Preferences.Default.Get(nameof(Password), "naco2026"); set => Preferences.Default.Set(nameof(Password), value); }
    public string ConnectionString => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = Database,
        UserID = User,
        Password = Password,
        TrustServerCertificate = true,
        Encrypt = true,
        ConnectTimeout = 15
    }.ConnectionString;
}
