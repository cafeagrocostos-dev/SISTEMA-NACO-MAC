namespace SistemaNacoMac.Services;

public sealed class SessionState
{
    public int? IdUsuario { get; private set; }
    public string Usuario { get; private set; } = "";
    public string NombreCompleto { get; private set; } = "";
    public string Rol { get; private set; } = "";
    public bool IsAuthenticated => IdUsuario.HasValue;
    public bool CanEdit => IsAuthenticated && !Rol.Equals("CONSULTA", StringComparison.OrdinalIgnoreCase);
    public bool IsAdmin => Rol.Equals("ADMIN", StringComparison.OrdinalIgnoreCase);

    public event Action? Changed;

    public void Open(int id, string usuario, string nombre, string rol)
    {
        IdUsuario = id; Usuario = usuario; NombreCompleto = nombre; Rol = rol; Changed?.Invoke();
    }

    public void Close()
    {
        IdUsuario = null; Usuario = NombreCompleto = Rol = ""; Changed?.Invoke();
    }
}
