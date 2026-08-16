# Revisión: Cierre de turno + IGV configurable

> Documento de contexto para la conversación. Última actualización: 2026-08-09.

## Proyectos involucrados

- **Agente de impresión:** `F:\MacSoft\ventory-printer` (C# .NET 8, ESC/POS).
- **POS:** `F:\MacSoft\ventoryPOS` (Laravel + React + Inertia).

---

## Problemas reportados

1. **Cierre de turno desordenado / ambiguo:** la impresión del cierre de turno sale con secciones poco claras y espaciado irregular.
2. **Cajero / caja con mucho espaciado:** en el ticket de venta, los campos `Cajero:` y `Caja:` se imprimen con espacios fijos desalineados.
3. **IGV confunde:** se quiere poder ocultar el desglose de IGV tanto en el ticket de venta como en el cierre de turno.

---

## Hallazgos del análisis

### Estructura actual del cierre de turno

Archivo principal: `F:\MacSoft\ventory-printer\Printing\ShiftClosureRenderer.cs`

Secciones actuales:

1. Header (logo + datos del negocio)
2. Turno (`CIERRE DE TURNO`, cajero, caja, fechas)
3. Resumen de ventas (subtotal, IGV, descuento, total)
4. Consolidado por método de pago
5. Consolidado de caja (efectivo)
6. Footer

Problemas detectados:

- Espaciado extra por `b.Feed()` entre secciones (especialmente antes del bloque de caja).
- El resumen siempre muestra IGV si `igv > 0`, sin opción a ocultarlo.
- Las líneas de cajero/caja no están compactadas con una sola estrategia de alineación.

### Ticket de venta

Archivo principal: `F:\MacSoft\ventory-printer\Printing\TicketRenderer.cs`

- Los campos `Fecha:`, `Cajero:`, `Caja:` se imprimen con strings fijos y espacios manuales.
- El IGV se muestra solo para documentos cuyo tipo contiene "BOLETA" o "FACTURA" (`DocumentoLlevaIgv`). No hay opción configurable.

### Configuración de empresa / ticketera

Archivo principal en POS: `F:\MacSoft\ventoryPOS\app\Http\Controllers\Configuracion\EmpresaController.php`

- La plantilla del ticket se guarda en `empresas.ticket_config` (JSON).
- Campos actuales: `cliente_celular`, `cliente_direccion`, `mostrar_ruc`, `logo_escala`, `pie`, `lineas_extra`.
- **No existe `mostrar_igv`.**
- Se edita en: `resources/js/Pages/Configuracion/Empresas.tsx`, sección **"Ticket de venta"**.

### Payloads de impresión

Archivo principal en POS: `F:\MacSoft\ventoryPOS\app\Services\TicketPrintService.php`

- `payloadDeVenta()`
- `payloadDeCotizacion()`
- `payloadDeEntregaAnticipo()`
- `payloadDeCierreTurno()`

Todos envían `totales.subtotal`, `totales.igv`, etc. Ninguno envía un flag para mostrar/ocultar IGV.

### Tipos del frontend

Archivo: `F:\MacSoft\ventoryPOS\resources\js\lib\ticketPrinter.ts`

- Define `TicketPayload` y `ShiftClosurePayload`.
- `NegocioInfo` no tiene campo `mostrarIgv`.

---

## Plan de cambios propuesto

### Opción elegida para el IGV

- **Un solo checkbox** en Configuración → Empresas → Ticket de venta:
  > **"Mostrar desglose de IGV en ticket y cierre de turno"**
- **Default:** desactivado (`false`).
- Afecta tanto al ticket de venta como al cierre de turno.
- Si en el futuro se requiere separar ticket vs. cierre, se puede dividir en dos flags fácilmente.

### Cambios en ventoryPOS

1. **`app/Http/Requests/Configuracion/EmpresaRequest.php`**
   - Agregar `'ticket_mostrar_igv' => 'boolean'`.

2. **`app/Http/Controllers/Configuracion/EmpresaController.php`**
   - Agregar `'mostrar_igv' => (bool) ($datos['ticket_mostrar_igv'] ?? false)` dentro de `ticket_config`.

3. **`resources/js/Pages/Configuracion/Empresas.tsx`**
   - Agregar `ticket_mostrar_igv: boolean` a `FormData` y `emptyForm`.
   - Leer `emp.ticket_config?.mostrar_igv ?? false` en `openEdit()`.
   - Agregar checkbox en la sección "Ticket de venta".

4. **`resources/js/lib/ticketPrinter.ts`**
   - Agregar `mostrarIgv?: boolean` a la interfaz `negocio`.

5. **`app/Services/TicketPrintService.php`**
   - Leer `$cfg['mostrar_igv'] ?? false`.
   - Incluir `'mostrarIgv' => (bool) ($cfg['mostrar_igv'] ?? false)` dentro del bloque `negocio` de todos los payloads.

### Cambios en VentoryPrint (agente C#)

1. **`Models/TicketPayload.cs` y `Models/ShiftClosurePayload.cs`**
   - Agregar `MostrarIgv` a `NegocioInfo`.

2. **`Printing/TicketRenderer.cs`**
   - Si `d.Negocio?.MostrarIgv ?? false` es falso, omitir la línea de IGV aunque sea boleta/factura.
   - Compactar / alinear `Fecha:`, `Cajero:`, `Caja:` usando `PadLabel` o similar.

3. **`Printing/ShiftClosureRenderer.cs`**
   - Si `d.Negocio?.MostrarIgv ?? false` es falso, omitir SUBTOTAL e IGV del resumen.
   - Reordenar secciones para que sean más claras.
   - Reducir espaciado innecesario (quitar `b.Feed()` extra).
   - Compactar el bloque de cajero/caja.

---

## Estructura visual propuesta para el cierre de turno (80 mm)

```
        FERRETERIA DE PRUEBA
        RUC: 20123456789
        Av. Prueba 123 - Chiclayo
        Telf: 987654321

        CIERRE DE TURNO
------------------------------------------------
Turno:    T-001 - Turno Mañana
Cajero:   CAJERA PRUEBA
Caja:     CAJA 1
Apertura: 09/08/2026 08:00 AM
Cierre:   09/08/2026 06:00 PM
------------------------------------------------
       RESUMEN DE VENTAS
------------------------------------------------
# Ventas:            45
Descuento:    S/    -50.00
TOTAL:        S/  1,670.00
------------------------------------------------
 CONSOLIDADO POR METODO DE PAGO
------------------------------------------------
EFECTIVO        23   S/    820.00
VISA            10   S/    400.00
...
TOTAL M.P.:          S/  1,670.00
------------------------------------------------
     CONSOLIDADO DE CAJA (EFECTIVO)
------------------------------------------------
Monto apertura:   S/    200.00
Ventas efectivo:S/    820.00
Entradas:        S/     50.00
Salidas:         S/    -30.00
------------------------------------------------
Efectivo esperado:  S/  1,040.00
Efectivo declarado: S/  1,035.00
Diferencia:         S/     -5.00
------------------------------------------------
        Reporte generado por ventoryPOS
```

> Si se activa "Mostrar IGV", el resumen incluiría `SUBTOTAL` e `IGV` debajo de `# Ventas`.

---

## Archivos a modificar

### ventoryPOS

- `app/Http/Requests/Configuracion/EmpresaRequest.php`
- `app/Http/Controllers/Configuracion/EmpresaController.php`
- `app/Services/TicketPrintService.php`
- `resources/js/Pages/Configuracion/Empresas.tsx`
- `resources/js/lib/ticketPrinter.ts`
- `resources/js/types/index.ts` (si es necesario tipar `ticket_config`)

### VentoryPrint

- `Models/TicketPayload.cs`
- `Models/ShiftClosurePayload.cs`
- `Printing/TicketRenderer.cs`
- `Printing/ShiftClosureRenderer.cs`
- `Cli/TestShiftClosureCli.cs` (opcional: agregar `MostrarIgv` al payload de prueba)

---

## Pendientes de decisión

1. ¿Confirmado **un solo checkbox** para IGV en ticket + cierre?
2. ¿Se quiere ocultar IGV también en cotizaciones y entregas de anticipo, o solo en venta + cierre?
   - Propuesta actual: aplicar a todos (venta, cotización, entrega, cierre) por consistencia.
3. ¿Se quiere mostrar el `SUBTOTAL` cuando el IGV está oculto?
   - Propuesta actual: no, solo `# Ventas`, `Descuento` (si aplica) y `TOTAL`.

---

## Notas técnicas

- El agente VentoryPrint se configura localmente en `%APPDATA%\VentoryPrint\config.json`.
- La impresión usa ESC/POS; los espaciados se controlan con `EscPosBuilder.Feed()` y `Line()`.
- El payload del cierre se envía a `POST /print-shift-closure`.
- El payload del ticket se envía a `POST /print`.
