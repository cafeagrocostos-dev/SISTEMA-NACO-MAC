using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SistemaNacoMac.Models;

namespace SistemaNacoMac.Services;

public sealed class NacoDatabase(AppSettings settings, SessionState session)
{
    private SqlConnection Connection() => new(settings.ConnectionString);

    public async Task TestAsync()
    {
        await using var cn = Connection();
        await cn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT 1", cn);
        await cmd.ExecuteScalarAsync();
    }

    public async Task<(bool Ok, string Message)> LoginAsync(string user, string password)
    {
        try
        {
            await using var cn = Connection();
            await cn.OpenAsync();
            await using (var count = new SqlCommand("SELECT COUNT(*) FROM dbo.Usuarios", cn))
            {
                if (Convert.ToInt32(await count.ExecuteScalarAsync()) == 0)
                {
                    if (user.Trim().Length < 3 || password.Length < 6)
                        return (false, "Para crear el primer administrador use al menos 3 caracteres y una contraseña de 6.");
                    await using var create = new SqlCommand("INSERT INTO dbo.Usuarios(Usuario,PasswordHash,NombreCompleto,Rol,Activo) OUTPUT INSERTED.IdUsuario VALUES(@U,@H,@N,'ADMIN',1)", cn);
                    create.Parameters.Add("@U", SqlDbType.NVarChar, 50).Value = user.Trim();
                    create.Parameters.Add("@H", SqlDbType.NVarChar, 255).Value = PasswordSecurity.CreateHash(password);
                    create.Parameters.Add("@N", SqlDbType.NVarChar, 100).Value = user.Trim();
                    var id = Convert.ToInt32(await create.ExecuteScalarAsync());
                    session.Open(id, user.Trim(), user.Trim(), "ADMIN");
                    return (true, "Se creó el primer administrador.");
                }
            }
            await using var cmd = new SqlCommand("SELECT TOP(1) IdUsuario,PasswordHash,ISNULL(NombreCompleto,''),Rol FROM dbo.Usuarios WHERE Usuario=@U AND Activo=1", cn);
            cmd.Parameters.Add("@U", SqlDbType.NVarChar, 50).Value = user.Trim();
            await using var rd = await cmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync() || !PasswordSecurity.Verify(password, rd.GetString(1)))
                return (false, "Usuario o contraseña incorrectos.");
            session.Open(rd.GetInt32(0), user.Trim(), rd.GetString(2), rd.GetString(3));
            return (true, "Acceso correcto.");
        }
        catch (Exception ex) { return (false, "No se pudo conectar: " + ex.Message); }
    }

    public async Task<DashboardSummary> GetSummaryAsync()
    {
        await using var cn = Connection(); await cn.OpenAsync();
        const string sql = "SELECT COUNT(*) Productos,ISNULL(SUM(CantidadPallet),0) Pallets,ISNULL(SUM(TotalCajas),0) Cajas,(SELECT COUNT(*) FROM dbo.Movimientos WHERE FechaMovimiento>=CONVERT(date,GETDATE())) MovimientosHoy FROM dbo.Inventario WHERE TotalCajas>0";
        await using var cmd = new SqlCommand(sql, cn); await using var rd = await cmd.ExecuteReaderAsync(); await rd.ReadAsync();
        return new() { Productos = rd.GetInt32(0), Pallets = Convert.ToInt64(rd[1]), Cajas = Convert.ToInt64(rd[2]), MovimientosHoy = rd.GetInt32(3) };
    }

    public async Task<List<InventoryItem>> GetInventoryAsync(bool includeEmpty=false,bool includeLayout=false)
    {
        var result = new List<InventoryItem>();
        await using var cn = Connection(); await cn.OpenAsync();
        await EnsureCroquisLayoutAsync(cn);
        const string sql = "SELECT i.IdInventario,i.IdUbicacion,u.Codigo,i.NombreProducto,ISNULL(i.Medida,''),i.CantidadPallet,i.CajasPorPallet,i.TotalCajas FROM dbo.Inventario i JOIN dbo.Ubicaciones u ON u.IdUbicacion=i.IdUbicacion WHERE @ALL=1 OR i.TotalCajas>0 OR i.CantidadPallet>0 ORDER BY u.Codigo,i.NombreProducto";
        await using (var cmd = new SqlCommand(sql, cn))
        {
            cmd.Parameters.AddWithValue("@ALL",includeEmpty?1:0);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) result.Add(new() { IdInventario=rd.GetInt64(0), IdUbicacion=rd.GetInt32(1), Ubicacion=rd.GetString(2), Producto=rd.GetString(3), Medida=rd.GetString(4), Pallets=Convert.ToInt64(rd[5]), CajasPorPallet=Convert.ToInt32(rd[6]), Cajas=Convert.ToInt64(rd[7]) });
        }
        try
        {
            await using var pos = new SqlCommand("SELECT IdInventario,Posicion,Pallets,Cajas FROM dbo.NacoPosiciones ORDER BY Posicion", cn);
            await using var rd = await pos.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var item = result.FirstOrDefault(x => x.IdInventario == rd.GetInt64(0));
                if (item is not null) item.Posiciones.Add(new() { Posicion=rd.GetInt32(1), Pallets=rd.GetInt32(2), Cajas=rd.GetInt64(3) });
            }
        }
        catch (SqlException) { }
        await ApplyCroquisLayoutAsync(cn,result,includeLayout);
        return result;
    }

    private static async Task ApplyCroquisLayoutAsync(SqlConnection cn,List<InventoryItem> result,bool includeEmptyLocations)
    {
        await using (var layout = new SqlCommand("SELECT Codigo,Seccion,Orden,Profundidad,Invertido,LlenarDesdeAbajo FROM dbo.NacoCroquisConfiguracion WHERE Activa=1 ORDER BY Seccion,Orden",cn))
        {
            await using var rd=await layout.ExecuteReaderAsync();
            while(await rd.ReadAsync())
            {
                var code=rd.GetString(0);var matches=result.Where(x=>x.Ubicacion.Equals(code,StringComparison.OrdinalIgnoreCase)).ToList();
                if(matches.Count==0&&includeEmptyLocations){matches.Add(new(){Ubicacion=code});result.Add(matches[0]);}
                foreach(var item in matches){item.SeccionCroquis=rd.GetString(1);item.OrdenCroquis=rd.GetInt32(2);item.ProfundidadCroquis=rd.GetInt32(3);item.InvertidoCroquis=rd.GetBoolean(4);item.LlenarDesdeAbajoCroquis=rd.GetBoolean(5);}
            }
        }
    }

    private static async Task EnsureCroquisLayoutAsync(SqlConnection cn,SqlTransaction? tx=null)
    {
        const string sql="""
IF OBJECT_ID('dbo.NacoCroquisConfiguracion','U') IS NULL
BEGIN
 CREATE TABLE dbo.NacoCroquisConfiguracion(Codigo nvarchar(50) NOT NULL PRIMARY KEY,Seccion nvarchar(10) NOT NULL,Orden int NOT NULL,Profundidad int NOT NULL CONSTRAINT DF_NacoCroquisProfundidad DEFAULT 12 CHECK(Profundidad>0),Invertido bit NOT NULL CONSTRAINT DF_NacoCroquisInvertido DEFAULT 0,LlenarDesdeAbajo bit NOT NULL CONSTRAINT DF_NacoCroquisLlenarAbajo DEFAULT 0,Activa bit NOT NULL CONSTRAINT DF_NacoCroquisActiva DEFAULT 1,FechaActualizacion datetime2 NOT NULL CONSTRAINT DF_NacoCroquisFecha DEFAULT SYSDATETIME(),CONSTRAINT UQ_NacoCroquisConfiguracion UNIQUE(Seccion,Orden));
END;
;WITH numeros AS (SELECT 1 n UNION ALL SELECT n+1 FROM numeros WHERE n<56),secciones AS (SELECT N'A' s,0 inv,0 abajo UNION ALL SELECT N'B',1,1 UNION ALL SELECT N'C',0,0 UNION ALL SELECT N'D',1,1)
INSERT INTO dbo.NacoCroquisConfiguracion(Codigo,Seccion,Orden,Profundidad,Invertido,LlenarDesdeAbajo)
SELECT s+CONVERT(nvarchar(10),n),s,n,12,inv,abajo FROM secciones CROSS JOIN numeros WHERE NOT EXISTS(SELECT 1 FROM dbo.NacoCroquisConfiguracion c WHERE c.Codigo=s+CONVERT(nvarchar(10),n)) OPTION(MAXRECURSION 56);
;WITH ubicacionesCroquis AS (SELECT UPPER(LTRIM(RTRIM(u.Codigo))) Codigo,UPPER(LEFT(LTRIM(RTRIM(u.Codigo)),1)) Seccion,CASE WHEN LEN(SUBSTRING(LTRIM(RTRIM(u.Codigo)),2,20)) BETWEEN 1 AND 9 AND SUBSTRING(LTRIM(RTRIM(u.Codigo)),2,20) NOT LIKE '%[^0-9]%' THEN CONVERT(int,SUBSTRING(LTRIM(RTRIM(u.Codigo)),2,20)) END Orden FROM dbo.Ubicaciones u WHERE u.Activa=1)
INSERT INTO dbo.NacoCroquisConfiguracion(Codigo,Seccion,Orden,Profundidad,Invertido,LlenarDesdeAbajo)
SELECT u.Codigo,u.Seccion,u.Orden,12,CASE WHEN u.Seccion IN ('B','D') THEN 1 ELSE 0 END,CASE WHEN u.Seccion IN ('B','D') THEN 1 ELSE 0 END FROM ubicacionesCroquis u WHERE u.Seccion IN ('A','B','C','D') AND u.Orden IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.NacoCroquisConfiguracion c WHERE c.Codigo=u.Codigo);
""";
        await using var cmd=new SqlCommand(sql,cn,tx);cmd.CommandTimeout=60;await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> GetLocationsAsync()
    {
        var result = new List<string>(); await using var cn = Connection(); await cn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT Codigo FROM dbo.Ubicaciones WHERE Activa=1 ORDER BY Codigo", cn); await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) result.Add(rd.GetString(0)); return result;
    }

    public async Task<List<ProductCatalogItem>> GetProductCatalogAsync()
    {
        var result=new List<ProductCatalogItem>();await using var cn=Connection();await cn.OpenAsync();await using var cmd=new SqlCommand("SELECT NombreProducto,ISNULL(Medida,''),MAX(CajasPorPallet) FROM dbo.Inventario WHERE NULLIF(LTRIM(RTRIM(NombreProducto)),'') IS NOT NULL GROUP BY NombreProducto,ISNULL(Medida,'') ORDER BY 1,2",cn);await using var rd=await cmd.ExecuteReaderAsync();while(await rd.ReadAsync())result.Add(new(){Producto=rd.GetString(0),Medida=rd.GetString(1),CajasPorPallet=Convert.ToInt32(rd[2])});return result;
    }

    public async Task<List<MovementRow>> GetMovementsAsync(DateTime from, DateTime to, string type="TODOS")
    {
        var result = new List<MovementRow>(); await using var cn = Connection(); await cn.OpenAsync();
        const string sql = "SELECT m.IdMovimiento,m.FechaMovimiento,m.TipoMovimiento,COALESCE(m.Contenedor,m.Orden,''),ISNULL(SUM(d.CantidadPallet),0),ISNULL(SUM(d.TotalCajas),0),ISNULL(u.Usuario,'') FROM dbo.Movimientos m LEFT JOIN dbo.DetalleMovimiento d ON d.IdMovimiento=m.IdMovimiento LEFT JOIN dbo.Usuarios u ON u.IdUsuario=m.IdUsuario WHERE m.FechaMovimiento>=@D AND m.FechaMovimiento<@H AND (@T='TODOS' OR m.TipoMovimiento=@T) GROUP BY m.IdMovimiento,m.FechaMovimiento,m.TipoMovimiento,m.Contenedor,m.Orden,u.Usuario ORDER BY m.FechaMovimiento DESC";
        await using var cmd = new SqlCommand(sql, cn); cmd.Parameters.AddWithValue("@D", from.Date); cmd.Parameters.AddWithValue("@H", to.Date.AddDays(1)); cmd.Parameters.AddWithValue("@T", type);
        await using var rd = await cmd.ExecuteReaderAsync(); while (await rd.ReadAsync()) result.Add(new(){IdMovimiento=rd.GetInt64(0),FechaMovimiento=rd.GetDateTime(1),TipoMovimiento=rd.GetString(2),Referencia=rd.GetString(3),Pallets=Convert.ToInt64(rd[4]),Cajas=Convert.ToInt64(rd[5]),Usuario=rd.GetString(6)}); return result;
    }

    public async Task<List<OperationLine>> GetMovementDetailsAsync(long id)
    {
        var result = new List<OperationLine>(); await using var cn=Connection(); await cn.OpenAsync();
        const string sql="SELECT ISNULL(uo.Codigo,''),ISNULL(ud.Codigo,''),d.NombreProducto,ISNULL(d.Medida,''),d.CantidadPallet,d.CajasPorPallet,d.CajasAdicionales,d.CajasFaltantes,d.TotalCajas FROM dbo.DetalleMovimiento d LEFT JOIN dbo.Ubicaciones uo ON uo.IdUbicacion=d.IdUbicacionOrigen LEFT JOIN dbo.Ubicaciones ud ON ud.IdUbicacion=d.IdUbicacionDestino WHERE d.IdMovimiento=@I ORDER BY d.IdDetalle";
        await using var cmd=new SqlCommand(sql,cn); cmd.Parameters.AddWithValue("@I",id); await using var rd=await cmd.ExecuteReaderAsync();
        while(await rd.ReadAsync()) result.Add(new(){Ubicacion=rd.GetString(0),MovidoA=rd.GetString(1),Producto=rd.GetString(2),Medida=rd.GetString(3),Pallets=Convert.ToInt32(rd[4]),CajasPorPallet=Convert.ToInt32(rd[5]),Adicionales=Convert.ToInt32(rd[6]),Faltantes=Convert.ToInt32(rd[7]),TotalCajas=Convert.ToInt32(rd[8])}); return result;
    }

    public async Task<MovementHeader> GetMovementHeaderAsync(long id)
    {
        await using var cn=Connection(); await cn.OpenAsync();
        const string sql="SELECT m.IdMovimiento,m.FechaMovimiento,m.TipoMovimiento,COALESCE(m.Contenedor,m.Orden,''),ISNULL(m.EnviadoA,''),COALESCE(NULLIF(m.RecibidoPor,''),m.EntregadoPor,''),ISNULL(m.Observacion,''),ISNULL(u.Usuario,'') FROM dbo.Movimientos m LEFT JOIN dbo.Usuarios u ON u.IdUsuario=m.IdUsuario WHERE m.IdMovimiento=@I";
        await using var cmd=new SqlCommand(sql,cn); cmd.Parameters.AddWithValue("@I",id); await using var rd=await cmd.ExecuteReaderAsync();
        if(!await rd.ReadAsync()) throw new InvalidOperationException("El movimiento solicitado no existe.");
        return new(){IdMovimiento=rd.GetInt64(0),Fecha=rd.GetDateTime(1),Tipo=rd.GetString(2),Referencia=rd.GetString(3),EnviadoA=rd.GetString(4),RecibidoPor=rd.GetString(5),Observacion=rd.GetString(6),Usuario=rd.GetString(7)};
    }

    public async Task<List<InventoryItem>> GetHistoricalPlanAsync(long id)
    {
        await using var cn=Connection(); await cn.OpenAsync();await EnsureCroquisLayoutAsync(cn);
        try
        {
            await using var cmd=new SqlCommand("SELECT Plano FROM dbo.NacoCroquisMovimientos WHERE IdMovimiento=@I",cn);cmd.Parameters.AddWithValue("@I",id);var json=Convert.ToString(await cmd.ExecuteScalarAsync());
            if(!string.IsNullOrWhiteSpace(json)){var plan=JsonSerializer.Deserialize<List<InventoryItem>>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??[];await ApplyCroquisLayoutAsync(cn,plan,true);return plan;}
        }
        catch(SqlException) { }
        return await GetInventoryAsync(false,true);
    }

    public async Task<long> SaveOperationAsync(string type, DateTime date, string reference, string sentTo, string receivedBy, string notes, IReadOnlyList<OperationLine> lines)
    {
        if (!session.CanEdit) throw new InvalidOperationException("Su usuario no puede guardar operaciones.");
        if (lines.Count==0) throw new InvalidOperationException("Añada al menos un producto.");
        if(type is not ("INGRESO" or "SALIDA" or "MOVIMIENTO" or "SALIDA POR DAÑO")) throw new InvalidOperationException("El tipo de operación no es válido.");
        await using var cn=Connection(); await cn.OpenAsync(); await EnsureCroquisLayoutAsync(cn); await using var tx=(SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await using var head=new SqlCommand("INSERT INTO dbo.Movimientos(TipoMovimiento,FechaMovimiento,Contenedor,Orden,EnviadoA,RecibidoPor,EntregadoPor,Observacion,IdUsuario) OUTPUT INSERTED.IdMovimiento VALUES(@T,@F,@R,@R,@E,@P,@G,@O,@U)",cn,tx);
            head.Parameters.AddWithValue("@T",type); head.Parameters.AddWithValue("@F",date); DbText(head,"@R",reference,100); DbText(head,"@E",sentTo,150); DbText(head,"@P",type=="INGRESO"?receivedBy:"",150); DbText(head,"@G",type=="INGRESO"?"":receivedBy,150); DbText(head,"@O",notes,500); head.Parameters.AddWithValue("@U",session.IdUsuario!.Value);
            var id=Convert.ToInt64(await head.ExecuteScalarAsync());
            foreach(var line in lines)
            {
                ValidateLine(line);
                var origin=type=="INGRESO"?await GetOrCreateLocationIdAsync(cn,tx,line.Ubicacion):await LocationIdAsync(cn,tx,line.Ubicacion);
                int? destination=type=="MOVIMIENTO"?await GetOrCreateLocationIdAsync(cn,tx,line.MovidoA):null;
                if(type=="INGRESO")
                {
                    var inventoryId=await AddStockAsync(cn,tx,origin,line);
                    await AddPositionsAsync(cn,tx,inventoryId,origin,line);
                }
                else
                {
                    await RemoveStockAsync(cn,tx,line.IdInventario,line);
                    await RemovePositionsAsync(cn,tx,line.IdInventario,line.Pallets,line.TotalCajas);
                    if(destination.HasValue)
                    {
                        var inventoryId=await AddStockAsync(cn,tx,destination.Value,line);
                        await AddPositionsAsync(cn,tx,inventoryId,destination.Value,line);
                    }
                }
                await using var detail=new SqlCommand("INSERT INTO dbo.DetalleMovimiento(IdMovimiento,IdUbicacionOrigen,IdUbicacionDestino,NombreProducto,Medida,CantidadPallet,CajasPorPallet,CajasAdicionales,CajasFaltantes,TotalCajas) VALUES(@I,@O,@D,@P,@M,@Q,@C,@A,@F,@T)",cn,tx);
                detail.Parameters.AddWithValue("@I",id); detail.Parameters.AddWithValue("@O",type=="INGRESO"?DBNull.Value:origin); detail.Parameters.AddWithValue("@D",destination.HasValue?destination.Value: type=="INGRESO"?origin:DBNull.Value); detail.Parameters.AddWithValue("@P",line.Producto); detail.Parameters.AddWithValue("@M",line.Medida); detail.Parameters.AddWithValue("@Q",line.Pallets); detail.Parameters.AddWithValue("@C",line.CajasPorPallet); detail.Parameters.AddWithValue("@A",line.Adicionales); detail.Parameters.AddWithValue("@F",line.Faltantes); detail.Parameters.AddWithValue("@T",line.TotalCajas); await detail.ExecuteNonQueryAsync();
            }
            await SavePlanAsync(cn,tx,id);
            await tx.CommitAsync(); await AuditAsync(type,"OPERACIÓN GUARDADA","Movimiento "+id); return id;
        }
        catch { await tx.RollbackAsync(); throw; }
    }

    public async Task<List<UserRow>> GetUsersAsync()
    {
        var result=new List<UserRow>(); await using var cn=Connection(); await cn.OpenAsync(); await using var cmd=new SqlCommand("SELECT IdUsuario,Usuario,ISNULL(NombreCompleto,''),Rol,Activo FROM dbo.Usuarios ORDER BY Usuario",cn); await using var rd=await cmd.ExecuteReaderAsync(); while(await rd.ReadAsync()) result.Add(new(){IdUsuario=rd.GetInt32(0),Usuario=rd.GetString(1),NombreCompleto=rd.GetString(2),Rol=rd.GetString(3),Activo=rd.GetBoolean(4)}); return result;
    }

    public async Task SaveUserAsync(string user,string name,string role,string password)
    {
        if(!session.IsAdmin) throw new InvalidOperationException("Solo un administrador puede gestionar usuarios."); if(user.Trim().Length<3||password.Length<6) throw new InvalidOperationException("Usuario mínimo 3 caracteres y contraseña mínimo 6.");
        await using var cn=Connection(); await cn.OpenAsync(); await using var cmd=new SqlCommand("INSERT INTO dbo.Usuarios(Usuario,PasswordHash,NombreCompleto,Rol,Activo) VALUES(@U,@H,@N,@R,1)",cn); cmd.Parameters.AddWithValue("@U",user.Trim()); cmd.Parameters.AddWithValue("@H",PasswordSecurity.CreateHash(password)); cmd.Parameters.AddWithValue("@N",name.Trim()); cmd.Parameters.AddWithValue("@R",role); await cmd.ExecuteNonQueryAsync();
    }

    public async Task ChangeUserPasswordAsync(int id,string password)
    {
        if(!session.IsAdmin)throw new InvalidOperationException("Solo un administrador puede cambiar contraseñas.");if(password.Length<6)throw new InvalidOperationException("La contraseña debe tener al menos 6 caracteres.");await using var cn=Connection();await cn.OpenAsync();await using var cmd=new SqlCommand("UPDATE dbo.Usuarios SET PasswordHash=@H WHERE IdUsuario=@I",cn);cmd.Parameters.AddWithValue("@H",PasswordSecurity.CreateHash(password));cmd.Parameters.AddWithValue("@I",id);if(await cmd.ExecuteNonQueryAsync()!=1)throw new InvalidOperationException("El usuario ya no existe.");
    }

    public async Task ToggleUserAsync(int id)
    {
        if(!session.IsAdmin)throw new InvalidOperationException("Solo un administrador puede cambiar usuarios.");if(session.IdUsuario==id)throw new InvalidOperationException("No puede desactivar su propia sesión.");await using var cn=Connection();await cn.OpenAsync();await using var cmd=new SqlCommand("UPDATE dbo.Usuarios SET Activo=CASE WHEN Activo=1 THEN 0 ELSE 1 END WHERE IdUsuario=@I",cn);cmd.Parameters.AddWithValue("@I",id);if(await cmd.ExecuteNonQueryAsync()!=1)throw new InvalidOperationException("El usuario ya no existe.");
    }

    public async Task<List<ProductSummary>> GetCatalogAsync()
    {
        var result=new List<ProductSummary>();await using var cn=Connection();await cn.OpenAsync();await using var cmd=new SqlCommand("SELECT LTRIM(RTRIM(NombreProducto)),LTRIM(RTRIM(ISNULL(Medida,''))),ISNULL(SUM(CantidadPallet),0),ISNULL(SUM(TotalCajas),0) FROM dbo.Inventario GROUP BY LTRIM(RTRIM(NombreProducto)),LTRIM(RTRIM(ISNULL(Medida,''))) ORDER BY 1,2",cn);await using var rd=await cmd.ExecuteReaderAsync();while(await rd.ReadAsync())result.Add(new(){Producto=rd.GetString(0),Medida=rd.GetString(1),Pallets=Convert.ToInt64(rd[2]),Cajas=Convert.ToInt64(rd[3])});return result;
    }

    public async Task RenameProductAsync(string product,string measure,string newProduct,string newMeasure)
    {
        if(!session.CanEdit)throw new InvalidOperationException("Su usuario no puede corregir productos.");newProduct=newProduct.Trim();newMeasure=newMeasure.Trim();if(newProduct.Length is 0 or >150||newMeasure.Length>50)throw new InvalidOperationException("Revise el nombre y la medida.");
        await using var cn=Connection();await cn.OpenAsync();await using var tx=(SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await using(var duplicate=new SqlCommand("SELECT COUNT(*) FROM dbo.Inventario WITH(UPDLOCK,HOLDLOCK) WHERE NombreProducto=@N AND ISNULL(Medida,'')=@NM AND NOT (NombreProducto=@P AND ISNULL(Medida,'')=@M)",cn,tx)){ProductParameters(duplicate,product,measure,newProduct,newMeasure);if(Convert.ToInt64(await duplicate.ExecuteScalarAsync())>0)throw new InvalidOperationException("Ya existe otro producto con ese nombre y medida.");}
            foreach(var pair in new[]{("Inventario","NombreProducto"),("DetalleMovimiento","NombreProducto"),("InventarioFisicoDetalleNaco","Producto")}){var sql=$"IF OBJECT_ID('dbo.{pair.Item1}','U') IS NOT NULL UPDATE dbo.{pair.Item1} SET {pair.Item2}=@N,Medida=@NM WHERE {pair.Item2}=@P AND ISNULL(Medida,'')=@M";await using var change=new SqlCommand(sql,cn,tx);ProductParameters(change,product,measure,newProduct,newMeasure);await change.ExecuteNonQueryAsync();}
            await tx.CommitAsync();await AuditAsync("CATÁLOGO","CORRECCIÓN DE PRODUCTO",product+" | "+measure+" → "+newProduct+" | "+newMeasure);
        }
        catch{await tx.RollbackAsync();throw;}
    }

    public async Task DeleteEmptyProductAsync(string product,string measure)
    {
        if(!session.CanEdit)throw new InvalidOperationException("Su usuario no puede eliminar productos.");await using var cn=Connection();await cn.OpenAsync();await using var tx=(SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await using(var stock=new SqlCommand("SELECT COUNT(*) FROM dbo.Inventario WITH(UPDLOCK,HOLDLOCK) WHERE NombreProducto=@P AND ISNULL(Medida,'')=@M AND (CantidadPallet<>0 OR TotalCajas<>0)",cn,tx)){stock.Parameters.AddWithValue("@P",product);stock.Parameters.AddWithValue("@M",measure);if(Convert.ToInt64(await stock.ExecuteScalarAsync())>0)throw new InvalidOperationException("El producto todavía tiene existencias.");}
            await using(var positions=new SqlCommand("DELETE p FROM dbo.NacoPosiciones p JOIN dbo.Inventario i ON i.IdInventario=p.IdInventario WHERE i.NombreProducto=@P AND ISNULL(i.Medida,'')=@M",cn,tx)){positions.Parameters.AddWithValue("@P",product);positions.Parameters.AddWithValue("@M",measure);await positions.ExecuteNonQueryAsync();}
            await using(var remove=new SqlCommand("DELETE FROM dbo.Inventario WHERE NombreProducto=@P AND ISNULL(Medida,'')=@M",cn,tx)){remove.Parameters.AddWithValue("@P",product);remove.Parameters.AddWithValue("@M",measure);if(await remove.ExecuteNonQueryAsync()==0)throw new InvalidOperationException("El producto ya no existe.");}
            await tx.CommitAsync();await AuditAsync("CATÁLOGO","PRODUCTO ELIMINADO",product+" | "+measure);
        }
        catch{await tx.RollbackAsync();throw;}
    }

    public async Task CleanTestDataAsync()
    {
        if(!session.IsAdmin) throw new InvalidOperationException("Solo un administrador puede limpiar datos."); await using var cn=Connection(); await cn.OpenAsync(); await using var tx=(SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable);
        try { string[] commands=["IF OBJECT_ID('dbo.NacoCroquisAcciones','U') IS NOT NULL DELETE FROM dbo.NacoCroquisAcciones","IF OBJECT_ID('dbo.NacoCroquisMovimientos','U') IS NOT NULL DELETE FROM dbo.NacoCroquisMovimientos","IF OBJECT_ID('dbo.NacoPosiciones','U') IS NOT NULL DELETE FROM dbo.NacoPosiciones","IF OBJECT_ID('dbo.InventarioFisicoDetalleNaco','U') IS NOT NULL DELETE FROM dbo.InventarioFisicoDetalleNaco","IF OBJECT_ID('dbo.InventariosFisicosNaco','U') IS NOT NULL DELETE FROM dbo.InventariosFisicosNaco","DELETE FROM dbo.DetalleMovimiento","DELETE FROM dbo.Inventario","DELETE FROM dbo.Movimientos","DELETE FROM dbo.Historial"]; foreach(var sql in commands){await using var cmd=new SqlCommand(sql,cn,tx); await cmd.ExecuteNonQueryAsync();} await tx.CommitAsync(); } catch { await tx.RollbackAsync(); throw; }
    }

    public async Task ImportInitialInventoryAsync(IReadOnlyList<MigrationLine> lines)
    {
        if(!session.IsAdmin)throw new InvalidOperationException("Solo un administrador puede incorporar el inventario inicial.");
        if(lines.Count==0)throw new InvalidOperationException("Cargue una plantilla antes de incorporar.");
        await using var cn=Connection();await cn.OpenAsync();await using var tx=(SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await using(var check=new SqlCommand("SELECT COUNT(*) FROM dbo.Inventario WITH(UPDLOCK,HOLDLOCK)",cn,tx))if(Convert.ToInt64(await check.ExecuteScalarAsync())!=0)throw new InvalidOperationException("El inventario debe estar vacío antes de incorporar el inventario inicial.");
            foreach(var item in lines)
            {
                var location=await GetOrCreateLocationIdAsync(cn,tx,item.Ubicacion);
                var operation=new OperationLine{Ubicacion=item.Ubicacion,Producto=item.Producto,Medida=item.Medida,Pallets=item.Pallets,CajasPorPallet=item.CajasPorPallet,Adicionales=Math.Max(0,item.AjusteCajas),Faltantes=Math.Max(0,-item.AjusteCajas),TotalCajas=item.TotalCajas,Distribucion=item.Distribucion};
                ValidateLine(operation);var id=await AddStockAsync(cn,tx,location,operation);await AddPositionsAsync(cn,tx,id,location,operation);
            }
            await tx.CommitAsync();await AuditAsync("MIGRACIÓN","INVENTARIO INICIAL INCORPORADO","Registros: "+lines.Count);
        }
        catch{await tx.RollbackAsync();throw;}
    }

    public async Task<List<PhysicalSnapshot>> GetPhysicalSnapshotsAsync()
    {
        var result=new List<PhysicalSnapshot>(); await using var cn=Connection(); await cn.OpenAsync();
        await using var cmd=new SqlCommand("SELECT IdInventarioFisico,Fecha,Usuario,Estado,ISNULL(QuienHizo,''),ISNULL(QuienAutoriza,'') FROM dbo.InventariosFisicosNaco ORDER BY Fecha DESC",cn); await using var rd=await cmd.ExecuteReaderAsync();
        while(await rd.ReadAsync())result.Add(new(){IdInventarioFisico=rd.GetInt64(0),Fecha=rd.GetDateTime(1),Usuario=rd.GetString(2),Estado=rd.GetString(3),QuienHizo=rd.GetString(4),QuienAutoriza=rd.GetString(5)}); return result;
    }

    public async Task<long> SavePhysicalInventoryAsync(DateTime date,string madeBy,string authorizedBy,string notes,IReadOnlyList<PhysicalCount> counts)
    {
        if(!session.CanEdit)throw new InvalidOperationException("Su usuario no puede guardar inventarios físicos."); if(counts.Count==0)throw new InvalidOperationException("No hay productos para contar.");
        await using var cn=Connection();await cn.OpenAsync();await using var tx=(SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable);
        try{await using var head=new SqlCommand("INSERT INTO dbo.InventariosFisicosNaco(Fecha,FechaBase,Usuario,Observacion,Estado,QuienHizo,QuienAutoriza) OUTPUT INSERTED.IdInventarioFisico VALUES(@F,GETDATE(),@U,@O,'CALCULADO',@H,@A)",cn,tx);head.Parameters.AddWithValue("@F",date);head.Parameters.AddWithValue("@U",session.Usuario);head.Parameters.AddWithValue("@O",notes??"");head.Parameters.AddWithValue("@H",madeBy??"");head.Parameters.AddWithValue("@A",authorizedBy??"");var id=Convert.ToInt64(await head.ExecuteScalarAsync());foreach(var c in counts){if(!c.Esperado.HasValue)throw new InvalidOperationException("Pulse CALCULAR antes de guardar.");await using var detail=new SqlCommand("INSERT INTO dbo.InventarioFisicoDetalleNaco(IdInventarioFisico,Ubicacion,Distribucion,Producto,Medida,Esperado,Contado,Diferencia,PalletsContados,CajasPorPallet,CajasSueltas) VALUES(@I,@U,@R,@P,@M,@E,@C,@D,@PC,@CPP,@S)",cn,tx);detail.Parameters.AddWithValue("@I",id);detail.Parameters.AddWithValue("@U",c.Item.Ubicacion);detail.Parameters.AddWithValue("@R",c.Distribucion);detail.Parameters.AddWithValue("@P",c.Item.Producto);detail.Parameters.AddWithValue("@M",c.Item.Medida);detail.Parameters.AddWithValue("@E",c.Esperado.Value);detail.Parameters.AddWithValue("@C",c.Contado);detail.Parameters.AddWithValue("@D",c.Diferencia!.Value);detail.Parameters.AddWithValue("@PC",c.PalletsContados);detail.Parameters.AddWithValue("@CPP",c.Item.CajasPorPallet);detail.Parameters.AddWithValue("@S",c.CajasSueltas);await detail.ExecuteNonQueryAsync();}await tx.CommitAsync();return id;}catch{await tx.RollbackAsync();throw;}
    }

    public async Task<(PhysicalSnapshot Header,List<PhysicalCount> Counts,string Notes)> GetPhysicalInventoryAsync(long id)
    {
        await using var cn=Connection();await cn.OpenAsync();PhysicalSnapshot? header=null;string notes="";await using(var cmd=new SqlCommand("SELECT IdInventarioFisico,Fecha,Usuario,Estado,ISNULL(QuienHizo,''),ISNULL(QuienAutoriza,''),ISNULL(Observacion,'') FROM dbo.InventariosFisicosNaco WHERE IdInventarioFisico=@I",cn)){cmd.Parameters.AddWithValue("@I",id);await using var rd=await cmd.ExecuteReaderAsync();if(await rd.ReadAsync()){header=new(){IdInventarioFisico=rd.GetInt64(0),Fecha=rd.GetDateTime(1),Usuario=rd.GetString(2),Estado=rd.GetString(3),QuienHizo=rd.GetString(4),QuienAutoriza=rd.GetString(5)};notes=rd.GetString(6);}}
        if(header is null)throw new InvalidOperationException("No se encontró el inventario físico.");var counts=new List<PhysicalCount>();await using(var cmd=new SqlCommand("SELECT Ubicacion,Distribucion,Producto,Medida,Esperado,PalletsContados,CajasPorPallet,CajasSueltas FROM dbo.InventarioFisicoDetalleNaco WHERE IdInventarioFisico=@I ORDER BY IdDetalle",cn)){cmd.Parameters.AddWithValue("@I",id);await using var rd=await cmd.ExecuteReaderAsync();while(await rd.ReadAsync())counts.Add(new(){Item=new(){Ubicacion=rd.GetString(0),Producto=rd.GetString(2),Medida=rd.GetString(3),CajasPorPallet=rd.IsDBNull(6)?0:Convert.ToInt32(rd[6])},Distribucion=rd.IsDBNull(1)?"":rd.GetString(1),Esperado=rd.IsDBNull(4)?0:rd.GetInt64(4),PalletsContados=rd.IsDBNull(5)?0:Convert.ToInt64(rd[5]),CajasSueltas=rd.IsDBNull(7)?0:Convert.ToInt64(rd[7])});}return(header,counts,notes);
    }

    public async Task IncorporatePhysicalInventoryAsync(long id)
    {
        if(!session.CanEdit)throw new InvalidOperationException("Su usuario no puede incorporar el inventario físico.");await using var cn=Connection();await cn.OpenAsync();await using var tx=(SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await using(var done=new SqlCommand("UPDATE dbo.InventariosFisicosNaco SET Estado='INCORPORADO',QuienAutoriza=@A,FechaIncorporado=GETDATE() WHERE IdInventarioFisico=@I AND Estado='CALCULADO'",cn,tx)){done.Parameters.AddWithValue("@I",id);done.Parameters.AddWithValue("@A",session.Usuario);if(await done.ExecuteNonQueryAsync()!=1)throw new InvalidOperationException("Este inventario ya fue incorporado o no existe.");}
            var details=new List<(string Location,string Distribution,string Product,string Measure,int Pallets,int BoxesPerPallet,int Total)>();await using(var read=new SqlCommand("SELECT Ubicacion,Distribucion,Producto,Medida,PalletsContados,CajasPorPallet,Contado FROM dbo.InventarioFisicoDetalleNaco WHERE IdInventarioFisico=@I ORDER BY IdDetalle",cn,tx)){read.Parameters.AddWithValue("@I",id);await using var rd=await read.ExecuteReaderAsync();while(await rd.ReadAsync())details.Add((rd.GetString(0),rd.GetString(1),rd.GetString(2),rd.GetString(3),Convert.ToInt32(rd[4]),Convert.ToInt32(rd[5]),Convert.ToInt32(rd[6])));}
            var locations=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);foreach(var code in details.Select(x=>x.Location).Distinct(StringComparer.OrdinalIgnoreCase)){var location=await GetOrCreateLocationIdAsync(cn,tx,code);locations[code]=location;await using var clear=new SqlCommand("DELETE FROM dbo.NacoPosiciones WHERE IdUbicacion=@U",cn,tx);clear.Parameters.AddWithValue("@U",location);await clear.ExecuteNonQueryAsync();}
            foreach(var item in details){var line=new OperationLine{Ubicacion=item.Location,Producto=item.Product,Medida=item.Measure,Pallets=item.Pallets,CajasPorPallet=item.BoxesPerPallet,TotalCajas=item.Total,Distribucion=item.Distribution};var location=locations[item.Location];long inventoryId;await using(var find=new SqlCommand("SELECT TOP(1) IdInventario FROM dbo.Inventario WITH(UPDLOCK,HOLDLOCK) WHERE IdUbicacion=@U AND NombreProducto=@P AND ISNULL(Medida,'')=@M",cn,tx)){find.Parameters.AddWithValue("@U",location);find.Parameters.AddWithValue("@P",item.Product);find.Parameters.AddWithValue("@M",item.Measure);var value=await find.ExecuteScalarAsync();if(value is null){await using var add=new SqlCommand("INSERT INTO dbo.Inventario(IdUbicacion,NombreProducto,Medida,CantidadPallet,CajasPorPallet,TotalCajas) OUTPUT INSERTED.IdInventario VALUES(@U,@P,@M,@Q,@C,@T)",cn,tx);StockParams(add,location,line);inventoryId=Convert.ToInt64(await add.ExecuteScalarAsync());}else{inventoryId=Convert.ToInt64(value);await using var update=new SqlCommand("UPDATE dbo.Inventario SET CantidadPallet=@Q,CajasPorPallet=@C,TotalCajas=@T,FechaActualizacion=GETDATE() WHERE IdInventario=@I",cn,tx);update.Parameters.AddWithValue("@Q",item.Pallets);update.Parameters.AddWithValue("@C",item.BoxesPerPallet);update.Parameters.AddWithValue("@T",item.Total);update.Parameters.AddWithValue("@I",inventoryId);await update.ExecuteNonQueryAsync();}}await AddPositionsAsync(cn,tx,inventoryId,location,line);}
            await tx.CommitAsync();
        }
        catch{await tx.RollbackAsync();throw;}
    }

    private static void ValidateLine(OperationLine line){if(string.IsNullOrWhiteSpace(line.Ubicacion)||string.IsNullOrWhiteSpace(line.Producto))throw new InvalidOperationException("Revise ubicación y producto."); var total=(long)line.Pallets*line.CajasPorPallet+line.Adicionales-line.Faltantes;if(total<=0||total!=line.TotalCajas)throw new InvalidOperationException("Las cantidades del producto no son válidas.");}
    private static async Task<int> LocationIdAsync(SqlConnection cn,SqlTransaction tx,string code){code=code.Trim().ToUpperInvariant(); await using var cmd=new SqlCommand("SELECT IdUbicacion FROM dbo.Ubicaciones WITH(UPDLOCK,HOLDLOCK) WHERE Codigo=@C AND Activa=1",cn,tx);cmd.Parameters.AddWithValue("@C",code);var value=await cmd.ExecuteScalarAsync();if(value is null)throw new InvalidOperationException("La ubicación "+code+" no existe o está inactiva.");return Convert.ToInt32(value);}
    private static async Task<int> GetOrCreateLocationIdAsync(SqlConnection cn,SqlTransaction tx,string code)
    {
        code=code.Trim().ToUpperInvariant();if(!System.Text.RegularExpressions.Regex.IsMatch(code,"^[A-D][1-9][0-9]*$"))throw new InvalidOperationException("Ubicación no válida: "+code+". Use códigos como A1, B12 o D56.");
        await using(var find=new SqlCommand("SELECT IdUbicacion,Activa FROM dbo.Ubicaciones WITH(UPDLOCK,HOLDLOCK) WHERE Codigo=@C",cn,tx)){find.Parameters.AddWithValue("@C",code);await using var rd=await find.ExecuteReaderAsync();if(await rd.ReadAsync()){if(!rd.GetBoolean(1))throw new InvalidOperationException("La ubicación "+code+" está inactiva.");return rd.GetInt32(0);}}
        await using var add=new SqlCommand("INSERT INTO dbo.Ubicaciones(Codigo,Descripcion,Activa) OUTPUT INSERTED.IdUbicacion VALUES(@C,'Creada desde captura',1)",cn,tx);add.Parameters.AddWithValue("@C",code);var id=Convert.ToInt32(await add.ExecuteScalarAsync());
        await EnsureCroquisLayoutAsync(cn,tx);return id;
    }
    private static async Task<long> AddStockAsync(SqlConnection cn,SqlTransaction tx,int location,OperationLine line)
    {
        await using(var find=new SqlCommand("SELECT TOP(1) IdInventario FROM dbo.Inventario WITH(UPDLOCK,HOLDLOCK) WHERE IdUbicacion=@U AND NombreProducto=@P AND ISNULL(Medida,'')=@M ORDER BY IdInventario",cn,tx))
        {
            find.Parameters.AddWithValue("@U",location);find.Parameters.AddWithValue("@P",line.Producto);find.Parameters.AddWithValue("@M",line.Medida);
            var value=await find.ExecuteScalarAsync();
            if(value is not null)
            {
                var id=Convert.ToInt64(value);await using var update=new SqlCommand("UPDATE dbo.Inventario SET CantidadPallet=CantidadPallet+@Q,CajasPorPallet=@C,TotalCajas=TotalCajas+@T,FechaActualizacion=GETDATE() WHERE IdInventario=@I",cn,tx);update.Parameters.AddWithValue("@I",id);update.Parameters.AddWithValue("@Q",line.Pallets);update.Parameters.AddWithValue("@C",line.CajasPorPallet);update.Parameters.AddWithValue("@T",line.TotalCajas);await update.ExecuteNonQueryAsync();return id;
            }
        }
        await using var insert=new SqlCommand("INSERT INTO dbo.Inventario(IdUbicacion,NombreProducto,Medida,CantidadPallet,CajasPorPallet,TotalCajas) OUTPUT INSERTED.IdInventario VALUES(@U,@P,@M,@Q,@C,@T)",cn,tx);StockParams(insert,location,line);return Convert.ToInt64(await insert.ExecuteScalarAsync());
    }
    private static async Task RemoveStockAsync(SqlConnection cn,SqlTransaction tx,long id,OperationLine line){await using var cmd=new SqlCommand("UPDATE dbo.Inventario SET CantidadPallet=CantidadPallet-@Q,TotalCajas=TotalCajas-@T,FechaActualizacion=GETDATE() WHERE IdInventario=@I AND CantidadPallet>=@Q AND TotalCajas>=@T",cn,tx);cmd.Parameters.AddWithValue("@I",id);cmd.Parameters.AddWithValue("@Q",line.Pallets);cmd.Parameters.AddWithValue("@T",line.TotalCajas);if(await cmd.ExecuteNonQueryAsync()!=1)throw new InvalidOperationException("No hay existencias suficientes para "+line.Producto);}
    private static void StockParams(SqlCommand cmd,int location,OperationLine line){cmd.Parameters.AddWithValue("@U",location);cmd.Parameters.AddWithValue("@P",line.Producto);cmd.Parameters.AddWithValue("@M",line.Medida);cmd.Parameters.AddWithValue("@Q",line.Pallets);cmd.Parameters.AddWithValue("@C",line.CajasPorPallet);cmd.Parameters.AddWithValue("@T",line.TotalCajas);}
    private static async Task AddPositionsAsync(SqlConnection cn,SqlTransaction tx,long inventoryId,int location,OperationLine line)
    {
        if(line.Pallets<=0)return;
        var pattern=line.Distribucion.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        if(pattern.Count==0){var pending=line.Pallets;while(pending>0){var group=Math.Min(4,pending);pattern.Add(group);pending-=group;}}
        if(pattern.Any(x=>x is <1 or >4)||pattern.Sum()!=line.Pallets)throw new InvalidOperationException("La distribución debe usar grupos de 1 a 4 y sumar todos los pallets.");
        await using var max=new SqlCommand("SELECT ISNULL(MAX(Posicion),0) FROM dbo.NacoPosiciones WITH(UPDLOCK,HOLDLOCK) WHERE IdUbicacion=@U",cn,tx);max.Parameters.AddWithValue("@U",location);var position=Convert.ToInt32(await max.ExecuteScalarAsync());
        long remaining=line.TotalCajas;
        for(var index=0;index<pattern.Count;index++)
        {
            var pallets=pattern[index];var boxes=index==pattern.Count-1?remaining:Math.Min(remaining,(long)pallets*line.CajasPorPallet);remaining-=boxes;
            await using var add=new SqlCommand("INSERT INTO dbo.NacoPosiciones(IdInventario,IdUbicacion,Posicion,Pallets,Cajas) VALUES(@I,@U,@N,@P,@C)",cn,tx);add.Parameters.AddWithValue("@I",inventoryId);add.Parameters.AddWithValue("@U",location);add.Parameters.AddWithValue("@N",++position);add.Parameters.AddWithValue("@P",pallets);add.Parameters.AddWithValue("@C",boxes);await add.ExecuteNonQueryAsync();
        }
    }
    private static async Task RemovePositionsAsync(SqlConnection cn,SqlTransaction tx,long inventoryId,int pallets,long boxes)
    {
        if(pallets<=0&&boxes<=0)return;
        var cells=new List<(int Position,int Pallets,long Boxes)>();await using(var read=new SqlCommand("SELECT Posicion,Pallets,Cajas FROM dbo.NacoPosiciones WITH(UPDLOCK,HOLDLOCK) WHERE IdInventario=@I ORDER BY Posicion DESC",cn,tx)){read.Parameters.AddWithValue("@I",inventoryId);await using var rd=await read.ExecuteReaderAsync();while(await rd.ReadAsync())cells.Add((rd.GetInt32(0),rd.GetInt32(1),rd.GetInt64(2)));}
        var remainingPallets=pallets;long remainingBoxes=boxes;
        foreach(var cell in cells)
        {
            if(remainingPallets<=0&&remainingBoxes<=0)break;
            var takeP=Math.Min(remainingPallets,cell.Pallets);var takeB=Math.Min(remainingBoxes,cell.Boxes);var leftP=cell.Pallets-takeP;var leftB=cell.Boxes-takeB;remainingPallets-=takeP;remainingBoxes-=takeB;
            var sql=leftP==0&&leftB==0?"DELETE FROM dbo.NacoPosiciones WHERE IdInventario=@I AND Posicion=@N":"UPDATE dbo.NacoPosiciones SET Pallets=@P,Cajas=@C WHERE IdInventario=@I AND Posicion=@N";await using var change=new SqlCommand(sql,cn,tx);change.Parameters.AddWithValue("@I",inventoryId);change.Parameters.AddWithValue("@N",cell.Position);if(leftP!=0||leftB!=0){change.Parameters.AddWithValue("@P",leftP);change.Parameters.AddWithValue("@C",leftB);}await change.ExecuteNonQueryAsync();
        }
    }
    private static async Task SavePlanAsync(SqlConnection cn,SqlTransaction tx,long movementId)
    {
        try
        {
            var items=new List<InventoryItem>();await using(var read=new SqlCommand("SELECT i.IdInventario,i.IdUbicacion,u.Codigo,i.NombreProducto,ISNULL(i.Medida,''),i.CantidadPallet,i.CajasPorPallet,i.TotalCajas FROM dbo.Inventario i JOIN dbo.Ubicaciones u ON u.IdUbicacion=i.IdUbicacion WHERE i.TotalCajas>0 OR i.CantidadPallet>0 ORDER BY u.Codigo,i.NombreProducto",cn,tx)){await using var rd=await read.ExecuteReaderAsync();while(await rd.ReadAsync())items.Add(new(){IdInventario=rd.GetInt64(0),IdUbicacion=rd.GetInt32(1),Ubicacion=rd.GetString(2),Producto=rd.GetString(3),Medida=rd.GetString(4),Pallets=Convert.ToInt64(rd[5]),CajasPorPallet=Convert.ToInt32(rd[6]),Cajas=Convert.ToInt64(rd[7])});}
            await using(var pos=new SqlCommand("SELECT IdInventario,Posicion,Pallets,Cajas FROM dbo.NacoPosiciones ORDER BY Posicion",cn,tx)){await using var rd=await pos.ExecuteReaderAsync();while(await rd.ReadAsync()){var item=items.FirstOrDefault(x=>x.IdInventario==rd.GetInt64(0));if(item is not null)item.Posiciones.Add(new(){Posicion=rd.GetInt32(1),Pallets=rd.GetInt32(2),Cajas=rd.GetInt64(3)});}}
            await using var save=new SqlCommand("INSERT INTO dbo.NacoCroquisMovimientos(IdMovimiento,Plano) VALUES(@I,@J)",cn,tx);save.Parameters.AddWithValue("@I",movementId);save.Parameters.Add("@J",SqlDbType.NVarChar,-1).Value=JsonSerializer.Serialize(items);await save.ExecuteNonQueryAsync();
        }
        catch(SqlException ex) when(ex.Number is 208 or 207){ }
    }
    private static void DbText(SqlCommand cmd,string name,string value,int size)=>cmd.Parameters.Add(name,SqlDbType.NVarChar,size).Value=string.IsNullOrWhiteSpace(value)?DBNull.Value:value.Trim();
    private static void ProductParameters(SqlCommand cmd,string product,string measure,string newProduct,string newMeasure){cmd.Parameters.AddWithValue("@P",product);cmd.Parameters.AddWithValue("@M",measure);cmd.Parameters.AddWithValue("@N",newProduct);cmd.Parameters.AddWithValue("@NM",newMeasure);}
    private async Task AuditAsync(string module,string action,string detail){try{await using var cn=Connection();await cn.OpenAsync();await using var cmd=new SqlCommand("INSERT INTO dbo.Historial(IdUsuario,Modulo,Accion,Detalle,Equipo) VALUES(@U,@M,@A,@D,@E)",cn);cmd.Parameters.AddWithValue("@U",session.IdUsuario??(object)DBNull.Value);cmd.Parameters.AddWithValue("@M",module);cmd.Parameters.AddWithValue("@A",action);cmd.Parameters.AddWithValue("@D",detail);cmd.Parameters.AddWithValue("@E",DeviceInfo.Current.Name);await cmd.ExecuteNonQueryAsync();}catch{}}
}
