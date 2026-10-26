# 🔐 Guía de Configuración Segura de API Key y Proveedores de IA

Esta guía detalla los métodos recomendados para configurar y ejecutar **OpenBoardView AI** con tu API Key (Google Gemini u OpenAI) de forma 100% segura, asegurando que tus credenciales privadas **nunca** se filtren al historial de Git ni a repositorios públicos de GitHub.

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

## 🚀 4 Métodos Seguros para Configurar tu API Key

Elige cualquiera de los siguientes métodos según tu flujo de trabajo:

---

### Método 1: Al Vuelo desde la Interfaz Web (Popover en el Chat) — *El más rápido*

No necesitas editar ningún archivo ni reiniciar el servidor:
1. Abre tu navegador en **`http://127.0.0.1:5080`**.
2. En la cabecera del chat, verás el chip de estado de la IA (`[ 🔴 Offline ]`).
3. Haz clic sobre el chip o sobre el botón de reintentos (`🔄`): se desplegará una ventana emergente (*Popover*).
4. Pega tu API Key de Google Gemini (inicia con `AIzaSy...`) y presiona **Guardar**.
5. El sistema validará la clave al instante, conmutará el indicador a **🟢 Verde (Online)** y habilitará todas las herramientas de diagnóstico en tiempo real.

> [!NOTE]  
> La clave configurada por el popover se mantiene en la memoria del proceso del servidor durante la sesión de trabajo sin escribirse en archivos rastreados por Git.

---

### Método 2: Archivo Local Desacoplado (`appsettings.Local.json`) — *Recomendado para desarrollo persistente*

El servidor .NET está programado para cargar de forma prioritaria el archivo local `appsettings.Local.json` si existe, sobreescribiendo los valores por defecto sin modificar el archivo base `appsettings.json` que rastrea Git.

1. Crea el archivo `src/OpenBoardView.AI/appsettings.Local.json`:
   ```bash
   cat << 'EOF' > src/OpenBoardView.AI/appsettings.Local.json
   {
     "AiProvider": {
       "Provider": "Gemini",
       "Gemini": {
         "ApiKey": "TU_API_KEY_AQUI",
         "Model": "gemini-3.5-flash"
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

### Método 3: Variable de Entorno en Terminal (En Memoria / Sin Archivos)

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

### Método 4: Configuración Persistente en tu Perfil de Shell (`~/.zshrc` o `~/.bashrc`)

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

---

## 🤖 Modelo Recomendado y Cascada Resiliente

* **Modelo Predeterminado:** **`gemini-3.5-flash`**. Ofrece una latencia de ~1.5s a 3.0s con soporte nativo de llamadas a funciones (*Function Calling*).
* **Cascada Automática con Timeout de 15s:** Si el modelo seleccionado experimenta congestión o demoras superiores a 15 segundos, el sistema conmuta automáticamente a los modelos rápidos de respaldo:
  $$\text{gemini-3.5-flash} \longrightarrow \text{gemini-flash-lite-latest} \longrightarrow \text{gemini-3.5-flash-lite}$$
* **Soporte Local 100% Offline (Ollama):** Si prefieres no usar internet, instala [Ollama](https://ollama.ai) (`ollama run qwen2.5-coder:7b`) y configura `"Provider": "Ollama"` en `appsettings.Local.json`. El sistema detectará la GPU local y operará sin conexión.

---

## 🔍 Verificación de Seguridad antes de hacer Commit

Para tener certeza absoluta de que tus credenciales no están en el área de preparación (*staging*), ejecuta siempre:

```bash
git status
```

**Comprueba que:**
1. `appsettings.json` muestre únicamente el valor de plantilla (`"YOUR_GEMINI_API_KEY_HERE"`).
2. `appsettings.Local.json` **no** aparezca en la lista de archivos sin seguimiento (*Untracked files*).
