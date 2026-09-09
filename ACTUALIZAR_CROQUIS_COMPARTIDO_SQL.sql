USE [SISTEMA_NACO];
GO
SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 DECLARE @resultado int;
 EXEC @resultado=sys.sp_getapplock @Resource=N'Naco_Croquis_Esquema',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000;
 IF @resultado < 0 RAISERROR(N'No se pudo preparar el croquis.',16,1);
 IF OBJECT_ID('dbo.NacoPosiciones','U') IS NULL
 CREATE TABLE dbo.NacoPosiciones(
  IdInventario bigint NOT NULL, IdUbicacion int NOT NULL, Posicion int NOT NULL CHECK(Posicion>0),
  Pallets int NOT NULL CHECK(Pallets BETWEEN 0 AND 4), Cajas bigint NOT NULL CHECK(Cajas>=0),
  CONSTRAINT PK_NacoPosiciones PRIMARY KEY(IdUbicacion,Posicion)
 );
 IF OBJECT_ID('dbo.NacoCroquisMovimientos','U') IS NULL
 CREATE TABLE dbo.NacoCroquisMovimientos(IdMovimiento bigint NOT NULL PRIMARY KEY,Plano nvarchar(max) NOT NULL);
 IF OBJECT_ID('dbo.NacoCroquisAcciones','U') IS NULL
 CREATE TABLE dbo.NacoCroquisAcciones(IdAccion bigint IDENTITY(1,1) PRIMARY KEY,IdMovimiento bigint NOT NULL,Ubicacion nvarchar(50) NOT NULL,Texto nvarchar(250) NOT NULL,Tipo nvarchar(30) NOT NULL);
 IF OBJECT_ID('dbo.NacoCroquisConfiguracion','U') IS NULL
 CREATE TABLE dbo.NacoCroquisConfiguracion(
  Codigo nvarchar(50) NOT NULL PRIMARY KEY, Seccion nvarchar(10) NOT NULL, Orden int NOT NULL,
  Profundidad int NOT NULL CONSTRAINT DF_NacoCroquisProfundidad DEFAULT 12 CHECK(Profundidad>0),
  Invertido bit NOT NULL CONSTRAINT DF_NacoCroquisInvertido DEFAULT 0,
  LlenarDesdeAbajo bit NOT NULL CONSTRAINT DF_NacoCroquisLlenarAbajo DEFAULT 0,
  Activa bit NOT NULL CONSTRAINT DF_NacoCroquisActiva DEFAULT 1,
  FechaActualizacion datetime2 NOT NULL CONSTRAINT DF_NacoCroquisFecha DEFAULT SYSDATETIME(),
  CONSTRAINT UQ_NacoCroquisConfiguracion UNIQUE(Seccion,Orden)
 );
 ;WITH numeros AS (SELECT 1 n UNION ALL SELECT n+1 FROM numeros WHERE n<56),
 secciones AS (SELECT N'A' s,0 inv,0 abajo UNION ALL SELECT N'B',1,1 UNION ALL SELECT N'C',0,0 UNION ALL SELECT N'D',1,1)
 INSERT INTO dbo.NacoCroquisConfiguracion(Codigo,Seccion,Orden,Profundidad,Invertido,LlenarDesdeAbajo)
 SELECT s+CONVERT(nvarchar(10),n),s,n,12,inv,abajo FROM secciones CROSS JOIN numeros
 WHERE NOT EXISTS(SELECT 1 FROM dbo.NacoCroquisConfiguracion c WHERE c.Codigo=s+CONVERT(nvarchar(10),n)) OPTION(MAXRECURSION 56);
 ;WITH ubicacionesCroquis AS (
  SELECT UPPER(LTRIM(RTRIM(u.Codigo))) Codigo,UPPER(LEFT(LTRIM(RTRIM(u.Codigo)),1)) Seccion,
   CASE WHEN LEN(SUBSTRING(LTRIM(RTRIM(u.Codigo)),2,20)) BETWEEN 1 AND 9
          AND SUBSTRING(LTRIM(RTRIM(u.Codigo)),2,20) NOT LIKE '%[^0-9]%'
        THEN CONVERT(int,SUBSTRING(LTRIM(RTRIM(u.Codigo)),2,20)) END Orden
  FROM dbo.Ubicaciones u WHERE u.Activa=1)
 INSERT INTO dbo.NacoCroquisConfiguracion(Codigo,Seccion,Orden,Profundidad,Invertido,LlenarDesdeAbajo)
 SELECT u.Codigo,u.Seccion,u.Orden,12,CASE WHEN u.Seccion IN ('B','D') THEN 1 ELSE 0 END,CASE WHEN u.Seccion IN ('B','D') THEN 1 ELSE 0 END
 FROM ubicacionesCroquis u WHERE u.Seccion IN ('A','B','C','D') AND u.Orden IS NOT NULL
 AND NOT EXISTS(SELECT 1 FROM dbo.NacoCroquisConfiguracion c WHERE c.Codigo=u.Codigo);
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 DECLARE @mensaje nvarchar(2048);
 SET @mensaje=ERROR_MESSAGE();
 RAISERROR(N'%s',16,1,@mensaje);
END CATCH;

