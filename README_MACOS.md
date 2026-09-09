# Sistema NACO para macOS

Esta edición reconstruye el sistema NACO como aplicación .NET MAUI Blazor Hybrid para macOS. Conserva la conexión con la base de datos SQL Server existente y contiene ingreso, salida, movimiento, salida por daño, inventario, croquis, consultas, inventario físico, usuarios y limpieza controlada de datos de prueba.

La captura de ingreso acepta ubicaciones escritas manualmente y crea las nuevas al guardar. El catálogo de productos ofrece sugerencias y completa medida y cajas por pallet. Ingreso y movimiento muestran la distribución por fila, los pallets distribuidos y los pendientes. El inventario físico conserva el flujo de la versión Windows: captura, modificación, cálculo, filtros, guardado, búsqueda, incorporación, CSV e impresión. El croquis respeta las filas, colores y dirección de llenado de las secciones A/C y B/D. Los usuarios con rol CONSULTA pueden revisar el catálogo en modo de solo lectura.

## Requisitos de la Mac

- macOS 15 o posterior.
- Xcode actualizado.
- SDK de .NET 10.
- Carga de trabajo .NET MAUI.
- Acceso por red local o VPN al servidor SQL Server.

## Primera compilación

Abra Terminal dentro de esta carpeta y ejecute:

```bash
dotnet workload install maui
dotnet restore
dotnet build -f net10.0-maccatalyst
```

Para crear la edición de distribución:

```bash
dotnet publish -f net10.0-maccatalyst -c Release
```

La compilación Release incluye Mac Intel y Apple Silicon. La firma y el paquete final `.app` o `.pkg` se realizan en la Mac con el certificado de Apple correspondiente.

## Probar primero en Visual Studio para Windows

Abra `SistemaNacoMac.sln`, espere a que termine **Restaurando paquetes** y seleccione **Windows Machine** junto al botón verde. Después presione `F5`. En Windows, Visual Studio carga únicamente el destino Windows para evitar que intente preparar Mac Catalyst al mismo tiempo.

Si Visual Studio indica que falta una carga de trabajo, abra **Visual Studio Installer > Modificar**, marque **Desarrollo de la interfaz de usuario de aplicaciones multiplataforma de .NET** y complete la instalación. El proyecto requiere Visual Studio 2026 compatible con .NET 10.

## Conexión a SQL Server

Al iniciar, entre en **Migración y conexión** para revisar servidor, base de datos, usuario SQL y contraseña. Los valores iniciales corresponden a la instalación actual de NACO. La Mac debe poder localizar el servidor `CA01DB01`; si no lo resuelve, use el nombre DNS completo o la dirección IP. SQL Server debe aceptar conexiones TCP desde esa red.

Ejecute una sola vez `Database/INSTALAR_TABLAS_NACO_V6.sql` en SQL Server Management Studio si la base todavía no tiene las tablas auxiliares del croquis y del inventario físico.

## Exportar PDF

En **Croquis** o en el detalle de una consulta, pulse **Exportar PDF**. Se abre el cuadro de impresión de macOS; seleccione **PDF > Guardar como PDF**. El reporte de cada operación genera:

1. Cuadro del ingreso, salida, movimiento o salida por daño.
2. Croquis de las secciones A y B.
3. Croquis de las secciones C y D.

Los movimientos nuevos guardan su croquis histórico. Los movimientos antiguos que no tengan una copia guardada muestran el plano actual como compatibilidad.

## Limpieza de datos de prueba

La opción de la aplicación elimina los registros dentro de una transacción y conserva usuarios y ubicaciones. No ejecuta `DBCC CHECKIDENT`, por lo que funciona con el usuario limitado `naco_app`.

## Validación realizada

El proyecto fue compilado con destino `net10.0-maccatalyst` y con destino Windows de comprobación. Ambos terminaron con 0 errores y 0 advertencias. La conexión real al servidor y la firma Apple deben probarse desde la red y la Mac donde se instalará.
