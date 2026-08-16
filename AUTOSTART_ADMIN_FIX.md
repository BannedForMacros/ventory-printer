# VentoryPrint — Inicio automático con host `+` (administrador)

## Problema original

Cuando el agente se configuraba con **Host = `+`** (todas las interfaces de red) y se activaba el inicio automático con Windows, al reiniciar la PC el agente no iniciaba solo. Aparecía un error de permisos porque `HttpListener` (HTTP.sys) necesita un **URL ACL** (`netsh http add urlacl`) y el proceso autostart no tenía privilegios de administrador para crearlo.

## Solución implementada

Se agregó un nuevo botón en la UI y un modo de instalación elevado que:

1. Ejecuta `netsh http add urlacl ...` para registrar el permiso de red.
2. Crea una **tarea programada de Windows** (`VentoryPrintAgent`) con **“Ejecutar con los privilegios más altos”** (`/RL HIGHEST`).
3. La tarea se dispara al iniciar sesión del usuario (`/SC ONLOGON`) y ejecuta el `.exe` con `--autostart`.
4. Elimina la entrada antigua `HKCU\...\Run` si existía, para evitar duplicados.

## Archivos modificados

- `Cli/AutostartCli.cs` — lógica de autostart, tarea elevada, `netsh`, logs.
- `Program.cs` — nuevos parámetros `--install-autostart-elevated` y `--uninstall-autostart-elevated`.
- `Ui/MainForm.cs` — nuevo botón “Reparar inicio automático (admin)” y estado más detallado.

## Cómo usar

1. Abre:

   ```
   F:\MacSoft\ventory-printer\bin\Release\net8.0-windows\win-x64\publish\VentoryPrint.exe
   ```

2. Configura:
   - **Impresora**
   - **Host:** `+`
   - **Token** de la caja
   - **Puerto:** `9111` (o el que uses)

3. Guarda la configuración.

4. Presiona **“Reparar inicio automático (admin)”**.

5. Acepta la ventana de UAC de Windows.

6. El estado debe cambiar a:

   ```
   Estado: ACTIVO (admin)
   ```

7. Cierra VentoryPrint.

8. Apaga y enciende la PC.

9. Al iniciar sesión, debe aparecer el ícono de VentoryPrint junto al reloj y el agente escuchar en `http://+:9111/`.

## Cómo verificar desde consola (como administrador)

Abre PowerShell como administrador y ejecuta:

```powershell
cd "F:\MacSoft\ventory-printer\bin\Release\net8.0-windows\win-x64\publish"
.\VentoryPrint.exe --install-autostart-elevated
```

Salida esperada si todo funciona:

```
Autostart instalado con privilegios de administrador.
El agente arrancara solo al iniciar sesion, incluso con host '+' (todas las interfaces).
```

Ver estado:

```powershell
.\VentoryPrint.exe --autostart-status
```

Salida esperada:

```
Autostart: ACTIVO (tarea elevada -> admin).
```

## Desinstalar

Desde la UI, presiona **“Desactivar autostart”**. Si usaste el modo admin, también puedes ejecutar como administrador:

```powershell
.\VentoryPrint.exe --uninstall-autostart-elevated
```

## Diagnóstico

Si el botón “Reparar inicio automático (admin)” falla, el error exacto se guarda en:

```
%LOCALAPPDATA%\VentoryPrint\logs\autostart-install.log
```

Y también puedes ejecutar el comando elevado manualmente en consola para ver el mensaje exacto.

### Errores comunes

- `netsh devolvio el codigo 1. Mensaje: Error al agregar la reserva de direccion URL. Error: 183`
  - Significa que el permiso de red ya existía. El parche ya trata este caso como exitoso y continúa creando la tarea programada.
- `schtasks devolvio el codigo 1.`
  - La consola no tiene privilegios de administrador. Ejecuta PowerShell / CMD como administrador.

## Nota importante para deploy a cajas

La versión en `VentoryPrint.csproj` sigue siendo `1.2.1`. Si quieres que las cajas se actualicen solas vía el auto-updater, edita la versión (por ejemplo a `1.2.2`) y ejecuta:

```powershell
.\publish-agent.ps1
```

Luego sube `public/agent/` al servidor.
