using SistemaNacoMac.Models;

namespace SistemaNacoMac.Services;

public static class CroquisProjection
{
    public static List<InventoryItem> Project(IReadOnlyList<InventoryItem> current,IReadOnlyList<OperationLine> lines,string type)
    {
        var result=current.Select(Clone).ToList();
        foreach(var line in lines)
        {
            if(type!="INGRESO")Remove(result,line);
            if(type=="INGRESO")Add(result,line.Ubicacion,line);
            else if(type=="MOVIMIENTO")Add(result,line.MovidoA,line);
        }
        return result.Where(x=>x.Pallets>0||x.Cajas>0).ToList();
    }

    private static InventoryItem Clone(InventoryItem item)=>new(){IdInventario=item.IdInventario,IdUbicacion=item.IdUbicacion,Ubicacion=item.Ubicacion,Producto=item.Producto,Medida=item.Medida,Pallets=item.Pallets,CajasPorPallet=item.CajasPorPallet,Cajas=item.Cajas,Posiciones=item.Posiciones.Select(x=>new PalletCell{Posicion=x.Posicion,Pallets=x.Pallets,Cajas=x.Cajas}).ToList()};

    private static void Add(List<InventoryItem> items,string location,OperationLine line)
    {
        var item=items.FirstOrDefault(x=>x.Ubicacion.Equals(location,StringComparison.OrdinalIgnoreCase)&&x.Producto.Equals(line.Producto,StringComparison.OrdinalIgnoreCase)&&x.Medida.Equals(line.Medida,StringComparison.OrdinalIgnoreCase));
        if(item is null){item=new(){Ubicacion=location,Producto=line.Producto,Medida=line.Medida,CajasPorPallet=line.CajasPorPallet};items.Add(item);}
        item.Pallets+=line.Pallets;item.Cajas+=line.TotalCajas;item.CajasPorPallet=line.CajasPorPallet;
        var parts=line.Distribucion.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(int.Parse).ToList();var position=items.Where(x=>x.Ubicacion.Equals(location,StringComparison.OrdinalIgnoreCase)).SelectMany(x=>x.Posiciones).Select(x=>x.Posicion).DefaultIfEmpty(0).Max();long remaining=line.TotalCajas;
        for(var index=0;index<parts.Count;index++){var pallets=parts[index];var boxes=index==parts.Count-1?remaining:Math.Min(remaining,(long)pallets*line.CajasPorPallet);remaining-=boxes;item.Posiciones.Add(new(){Posicion=++position,Pallets=pallets,Cajas=boxes});}
    }

    private static void Remove(List<InventoryItem> items,OperationLine line)
    {
        var item=items.FirstOrDefault(x=>x.IdInventario==line.IdInventario);if(item is null)return;item.Pallets=Math.Max(0,item.Pallets-line.Pallets);item.Cajas=Math.Max(0,item.Cajas-line.TotalCajas);var remainingPallets=line.Pallets;long remainingBoxes=line.TotalCajas;
        foreach(var cell in item.Posiciones.OrderByDescending(x=>x.Posicion).ToList()){if(remainingPallets<=0&&remainingBoxes<=0)break;var takeP=Math.Min(remainingPallets,cell.Pallets);var takeB=Math.Min(remainingBoxes,cell.Cajas);cell.Pallets-=takeP;cell.Cajas-=takeB;remainingPallets-=takeP;remainingBoxes-=takeB;if(cell.Pallets==0&&cell.Cajas==0)item.Posiciones.Remove(cell);}
    }
}
