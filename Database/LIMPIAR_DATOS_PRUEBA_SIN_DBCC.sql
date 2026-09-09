-- Limpieza segura para el usuario de aplicación. Conserva Usuarios y Ubicaciones.
-- No usa DBCC CHECKIDENT ni requiere permiso de propietario de la base.
USE [SISTEMA_NACO];
GO
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID('dbo.NacoCroquisAcciones','U') IS NOT NULL DELETE FROM dbo.NacoCroquisAcciones;
    IF OBJECT_ID('dbo.NacoCroquisMovimientos','U') IS NOT NULL DELETE FROM dbo.NacoCroquisMovimientos;
    IF OBJECT_ID('dbo.NacoPosiciones','U') IS NOT NULL DELETE FROM dbo.NacoPosiciones;
    IF OBJECT_ID('dbo.InventarioFisicoDetalleNaco','U') IS NOT NULL DELETE FROM dbo.InventarioFisicoDetalleNaco;
    IF OBJECT_ID('dbo.InventariosFisicosNaco','U') IS NOT NULL DELETE FROM dbo.InventariosFisicosNaco;
    DELETE FROM dbo.DetalleMovimiento;
    DELETE FROM dbo.Inventario;
    DELETE FROM dbo.Movimientos;
    DELETE FROM dbo.Historial;
    COMMIT;
    PRINT N'Datos de prueba eliminados. Usuarios y ubicaciones se conservaron.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    THROW;
END CATCH;
GO
