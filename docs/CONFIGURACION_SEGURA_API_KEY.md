# 🔐 Guía de Configuración Segura de API Key (Sin Exposición en GitHub)

Esta guía detalla los métodos recomendados para configurar y ejecutar **OpenBoardView AI** con tu API Key (Google Gemini u OpenAI) de forma 100% segura, asegurando que tus credenciales privadas **nunca** se suban ni se filtren al historial de Git ni a repositorios públicos de GitHub.

---

## 🛡️ Mecanismos de Protección Implementados en el Proyecto

El archivo [`.gitignore`](../.gitignore) del repositorio ya incluye reglas estrictas para ignorar automáticamente los siguientes archivos de credenciales locales:

```gitignore
# Exclusiones de seguridad para credenciales locales
appsettings.Local.json
appsettings.Development.json
.env
```

Esto garantiza que cualquier archivo que use esos nombres nunca será rastreado por `git add` ni aparecerá en `git status`.

---

## 🚀 Métodos para Ejecutar el Proyecto con tu API Key

Elige cualquiera de los siguientes **3 métodos seguros** según tu preferencia de flujo de trabajo:

---

### Método 1: Archivo Local Desacoplado (`appsettings.Local.json`) — *Recomendado*

El servidor .NET está programado para cargar de forma prioritaria el archivo local `appsettings.Local.json` si existe, sobreescribiendo los valores por defecto sin modificar el archivo base `appsettings.json` que rastrea Git.

1. Crea el archivo `src/OpenBoardView.AI/appsettings.Local.json`:
   ```bash
   cat << 'EOF' > src/OpenBoardView.AI/appsettings.Local.json
   {
     "AiProvider": {
       "Gemini": {
         "ApiKey": "TU_API_KEY_AQUI"
       }
     }
   }
   EOF
   ```
2. Ejecuta el proyecto normalmente:
   ```bash
   dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj
   ```

> [!TIP]
> Dado que `appsettings.Local.json` está en `.gitignore`, puedes realizar commits y `git push` con total tranquilidad; tus credenciales se mantendrán únicamente en tu máquina local.

---

### Método 2: Variable de Entorno en Terminal (En Memoria / Sin Archivos)

Si prefieres no guardar tu API Key en ningún archivo del disco del proyecto, puedes inyectarla directamente en la sesión de tu terminal al momento de ejecutar:

```bash
export GEMINI_API_KEY="TU_API_KEY_AQUI"
dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj
```

O en una sola línea (la variable solo vivirá durante la ejecución del proceso):

```bash
GEMINI_API_KEY="TU_API_KEY_AQUI" dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj
```

---

### Método 3: Configuración Persistente en tu Perfil de Shell (`~/.zshrc`)

Si utilizas OpenBoardView AI con frecuencia y quieres que cualquier terminal lo reconozca automáticamente sin tener que tipear la clave cada vez:

1. Abre tu archivo de perfil de usuario (en macOS por defecto es `~/.zshrc`):
   ```bash
   echo 'export GEMINI_API_KEY="TU_API_KEY_AQUI"' >> ~/.zshrc
   source ~/.zshrc
   ```
2. Ejecuta el proyecto desde cualquier terminal:
   ```bash
   dotnet run --project src/OpenBoardView.AI/OpenBoardView.AI.csproj
   ```

> [!NOTE]
> `~/.zshrc` reside en tu carpeta de usuario personal (`/Users/tu_usuario/`), completamente fuera de la carpeta del repositorio de Git, por lo que es imposible que se filtre a GitHub.

---

## 🔍 Verificación de Seguridad antes de hacer Commit

Para tener certeza absoluta de que tus credenciales no están en el área de preparación (*staging*), ejecuta siempre:

```bash
git status
```

**Comprueba que:**
- `appsettings.json` muestre únicamente el valor de plantilla (`"YOUR_GEMINI_API_KEY_HERE"`).
- `appsettings.Local.json` **no** aparezca en la lista de archivos sin seguimiento (*Untracked files*).
- Ningún archivo modificado contenga texto con tu clave privada.

---

## 🌐 Comprobación del Servicio Activo

Una vez ejecutado el servidor con cualquiera de los métodos anteriores:
1. Abre tu navegador en: **`http://127.0.0.1:5080`**
2. Envía cualquier consulta al chat del panel lateral (ejemplo: *"¿Está activo el motor IA?"*).
3. El agente responderá directamente utilizando el modelo Gemini sin mostrar el aviso de API Key ausente.
