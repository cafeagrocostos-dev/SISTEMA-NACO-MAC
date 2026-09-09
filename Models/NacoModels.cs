namespace SistemaNacoMac.Models;

public sealed class InventoryItem
{
    public long IdInventario { get; set; }
    public int IdUbicacion { get; set; }
    public string Ubicacion { get; set; } = "";
    public string Producto { get; set; } = "";
    public string Medida { get; set; } = "";
    public long Pallets { get; set; }
    public int CajasPorPallet { get; set; }
    public long Cajas { get; set; }
    public List<PalletCell> Posiciones { get; set; } = [];
    public string SeccionCroquis { get; set; } = "";
    public int OrdenCroquis { get; set; }
    public int ProfundidadCroquis { get; set; } = 12;
    public bool InvertidoCroquis { get; set; }
    public bool LlenarDesdeAbajoCroquis { get; set; }
}

public sealed class PalletCell
{
    public int Posicion { get; set; }
    public int Pallets { get; set; }
    public long Cajas { get; set; }
}

public sealed class OperationLine
{
    public long IdInventario { get; set; }
    public int IdOrigen { get; set; }
    public string Ubicacion { get; set; } = "";
    public string MovidoA { get; set; } = "";
    public string Producto { get; set; } = "";
    public string Medida { get; set; } = "";
    public int Pallets { get; set; }
    public int CajasPorPallet { get; set; }
    public int Adicionales { get; set; }
    public int Faltantes { get; set; }
    public int TotalCajas { get; set; }
    public string Distribucion { get; set; } = "";
}

public sealed class MovementRow
{
    public long IdMovimiento { get; set; }
    public DateTime FechaMovimiento { get; set; }
    public string TipoMovimiento { get; set; } = "";
    public string Referencia { get; set; } = "";
    public long Pallets { get; set; }
    public long Cajas { get; set; }
    public string Usuario { get; set; } = "";
}

public sealed class MovementHeader
{
    public long IdMovimiento { get; set; }
    public DateTime Fecha { get; set; }
    public string Tipo { get; set; } = "";
    public string Referencia { get; set; } = "";
    public string EnviadoA { get; set; } = "";
    public string RecibidoPor { get; set; } = "";
    public string Observacion { get; set; } = "";
    public string Usuario { get; set; } = "";
}

public sealed class UserRow
{
    public int IdUsuario { get; set; }
    public string Usuario { get; set; } = "";
    public string NombreCompleto { get; set; } = "";
    public string Rol { get; set; } = "";
    public bool Activo { get; set; }
}

public sealed class DashboardSummary
{
    public long Productos { get; set; }
    public long Pallets { get; set; }
    public long Cajas { get; set; }
    public long MovimientosHoy { get; set; }
}

public sealed class PhysicalSnapshot
{
    public long IdInventarioFisico { get; set; }
    public DateTime Fecha { get; set; }
    public string Usuario { get; set; } = "";
    public string Estado { get; set; } = "";
    public string QuienHizo { get; set; } = "";
    public string QuienAutoriza { get; set; } = "";
}

public sealed class PhysicalCount
{
    public InventoryItem Item { get; set; } = new();
    public long PalletsContados { get; set; }
    public long CajasSueltas { get; set; }
    public string Distribucion { get; set; } = "";
    public long? Esperado { get; set; }
    public long Contado => PalletsContados * Item.CajasPorPallet + CajasSueltas;
    public long? Diferencia => Esperado.HasValue ? Contado - Esperado.Value : null;
    public string Estado => !Diferencia.HasValue ? "PENDIENTE" : Diferencia<0 ? "FALTANTE" : Diferencia>0 ? "SOBRANTE" : "IGUAL";
}

public sealed class MigrationLine
{
    public string Ubicacion { get; set; } = "";
    public string Producto { get; set; } = "";
    public string Medida { get; set; } = "";
    public int Pallets { get; set; }
    public int CajasPorPallet { get; set; }
    public int AjusteCajas { get; set; }
    public string Distribucion { get; set; } = "";
    public int TotalCajas => checked(Pallets * CajasPorPallet + AjusteCajas);
}

public sealed class ProductSummary
{
    public string Producto { get; set; } = "";
    public string Medida { get; set; } = "";
    public long Pallets { get; set; }
    public long Cajas { get; set; }
}

public sealed class ProductCatalogItem
{
    public string Producto { get; set; } = "";
    public string Medida { get; set; } = "";
    public int CajasPorPallet { get; set; }
}
