#include "ObvAiBridge.h"
#include "BoardView.h"
#include "GUI/DPI.h"
#include "imgui/imgui.h"

#include <chrono>
#include <cstring>
#include <sstream>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#define CLOSESOCKET closesocket
#else
#include <arpa/inet.h>
#include <netdb.h>
#include <sys/socket.h>
#include <unistd.h>
#define CLOSESOCKET close
#endif

ObvAiBridge::ObvAiBridge() {
	m_chatHistory.push_back({
	    "assistant",
	    "Agente IA de Microelectronica (.NET 10 + Microsoft.Extensions.AI) listo.\n"
	    "Escribe tu consulta o selecciona una accion rapida para aislar cortos por cuadrantes (Q1-Q4) e iluminar componentes.",
	    ""});
}

ObvAiBridge::~ObvAiBridge() {
	Stop();
}

void ObvAiBridge::Start(const std::string &host, int port) {
	if (m_running.load()) return;
	m_host    = host;
	m_port    = port;
	m_running = true;
	m_wsThread = std::thread(&ObvAiBridge::WebSocketThreadFunc, this);
}

void ObvAiBridge::Stop() {
	m_running = false;
	if (m_wsThread.joinable()) {
		m_wsThread.join();
	}
}

std::string ObvAiBridge::ExtractJsonString(const std::string &json, const std::string &key) {
	std::string pattern = "\"" + key + "\":\"";
	auto pos            = json.find(pattern);
	if (pos == std::string::npos) {
		// Case-insensitive fallback for camelCase / PascalCase
		std::string capKey = key;
		if (!capKey.empty()) capKey[0] = toupper(capKey[0]);
		pattern = "\"" + capKey + "\":\"";
		pos     = json.find(pattern);
		if (pos == std::string::npos) return "";
	}
	pos += pattern.size();
	std::ostringstream out;
	for (size_t i = pos; i < json.size(); ++i) {
		if (json[i] == '\\' && i + 1 < json.size()) {
			char next = json[++i];
			if (next == 'n')
				out << '\n';
			else if (next == '"')
				out << '"';
			else
				out << next;
		} else if (json[i] == '"') {
			break;
		} else {
			out << json[i];
		}
	}
	return out.str();
}

static int ConnectTcpSocket(const std::string &host, int port) {
	int sock = socket(AF_INET, SOCK_STREAM, 0);
	if (sock < 0) return -1;

	struct sockaddr_in serv_addr {};
	serv_addr.sin_family = AF_INET;
	serv_addr.sin_port   = htons(port);
	inet_pton(AF_INET, host.c_str(), &serv_addr.sin_addr);

	if (connect(sock, (struct sockaddr *)&serv_addr, sizeof(serv_addr)) < 0) {
		CLOSESOCKET(sock);
		return -1;
	}
	return sock;
}

void ObvAiBridge::WebSocketThreadFunc() {
	while (m_running.load()) {
		int sock = ConnectTcpSocket(m_host, m_port);
		if (sock < 0) {
			m_wsConnected = false;
			std::this_thread::sleep_for(std::chrono::seconds(2));
			continue;
		}

		std::ostringstream req;
		req << "GET /ws/obv HTTP/1.1\r\n"
		    << "Host: " << m_host << ":" << m_port << "\r\n"
		    << "Upgrade: websocket\r\n"
		    << "Connection: Upgrade\r\n"
		    << "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
		    << "Sec-WebSocket-Version: 13\r\n\r\n";

		std::string reqStr = req.str();
		send(sock, reqStr.c_str(), reqStr.size(), 0);

		char buf[4096];
		int n = recv(sock, buf, sizeof(buf) - 1, 0);
		if (n <= 0 || std::string(buf, n).find("101") == std::string::npos) {
			CLOSESOCKET(sock);
			m_wsConnected = false;
			std::this_thread::sleep_for(std::chrono::seconds(2));
			continue;
		}

		m_wsConnected = true;

		while (m_running.load()) {
			uint8_t header[2];
			int r = recv(sock, reinterpret_cast<char *>(header), 2, 0);
			if (r <= 0) break;

			uint8_t opcode = header[0] & 0x0F;
			if (opcode == 0x08) break; // Close frame

			uint64_t payloadLen = header[1] & 0x7F;
			if (payloadLen == 126) {
				uint8_t ext[2];
				if (recv(sock, reinterpret_cast<char *>(ext), 2, 0) != 2) break;
				payloadLen = (ext[0] << 8) | ext[1];
			} else if (payloadLen == 127) {
				uint8_t ext[8];
				if (recv(sock, reinterpret_cast<char *>(ext), 8, 0) != 8) break;
				payloadLen = 0;
				for (int i = 0; i < 8; ++i) payloadLen = (payloadLen << 8) | ext[i];
			}

			std::string payload(payloadLen, '\0');
			size_t totalRead = 0;
			while (totalRead < payloadLen) {
				int chunk = recv(sock, &payload[totalRead], payloadLen - totalRead, 0);
				if (chunk <= 0) break;
				totalRead += chunk;
			}

			if (totalRead == payloadLen && opcode == 0x01) {
				ObvAiCommand cmd;
				cmd.action    = ExtractJsonString(payload, "action");
				cmd.component = ExtractJsonString(payload, "component");
				cmd.net       = ExtractJsonString(payload, "net");
				cmd.quadrant  = ExtractJsonString(payload, "quadrant");
				cmd.message   = ExtractJsonString(payload, "message");

				if (!cmd.component.empty() || !cmd.net.empty()) {
					std::lock_guard<std::mutex> lock(m_mutex);
					m_pendingCommands.push_back(cmd);
				}
			}
		}

		m_wsConnected = false;
		CLOSESOCKET(sock);
		std::this_thread::sleep_for(std::chrono::seconds(2));
	}
}

void ObvAiBridge::SendPromptAsync(const std::string &prompt) {
	if (prompt.empty() || m_queryInFlight.load()) return;

	{
		std::lock_guard<std::mutex> lock(m_mutex);
		m_chatHistory.push_back({"user", prompt, ""});
	}

	m_queryInFlight = true;
	std::thread([this, prompt]() {
		int sock = ConnectTcpSocket(m_host, m_port);
		if (sock < 0) {
			std::lock_guard<std::mutex> lock(m_mutex);
			m_chatHistory.push_back({"assistant", "[Error]: No se pudo conectar al servidor .NET 10 en " + m_host + ":" + std::to_string(m_port), ""});
			m_queryInFlight = false;
			return;
		}

		std::string escapedPrompt;
		for (char c : prompt) {
			if (c == '"')
				escapedPrompt += "\\\"";
			else if (c == '\n')
				escapedPrompt += "\\n";
			else
				escapedPrompt += c;
		}

		std::string body = "{\"prompt\":\"" + escapedPrompt + "\"}";
		std::ostringstream req;
		req << "POST /api/agent/query HTTP/1.1\r\n"
		    << "Host: " << m_host << ":" << m_port << "\r\n"
		    << "Content-Type: application/json\r\n"
		    << "Content-Length: " << body.size() << "\r\n"
		    << "Connection: close\r\n\r\n"
		    << body;

		std::string reqStr = req.str();
		send(sock, reqStr.c_str(), reqStr.size(), 0);

		std::string response;
		char buf[4096];
		int n;
		while ((n = recv(sock, buf, sizeof(buf), 0)) > 0) {
			response.append(buf, n);
		}
		CLOSESOCKET(sock);

		auto bodyPos = response.find("\r\n\r\n");
		std::string jsonBody = (bodyPos != std::string::npos) ? response.substr(bodyPos + 4) : response;

		std::string answerText   = ExtractJsonString(jsonBody, "responseText");
		std::string inspectedCmp = ExtractJsonString(jsonBody, "inspectedComponent");
		std::string inspectedNet = ExtractJsonString(jsonBody, "inspectedNet");

		if (answerText.empty()) {
			answerText = "Respuesta recibida del motor IA.";
		}

		{
			std::lock_guard<std::mutex> lock(m_mutex);
			m_chatHistory.push_back({"assistant", answerText, inspectedCmp});
			if (!inspectedCmp.empty()) {
				m_pendingCommands.push_back({"highlight", inspectedCmp, inspectedNet, "", "Seleccionado por diagnostico IA"});
			}
		}

		m_queryInFlight = false;
	}).detach();
}

void ObvAiBridge::UpdateAndRender(BoardView &bv, bool *p_open) {
	// 1. Ejecutar comandos WebSocket pendientes sobre la vista nativa de OpenBoardView
	std::vector<ObvAiCommand> cmds;
	{
		std::lock_guard<std::mutex> lock(m_mutex);
		cmds.swap(m_pendingCommands);
	}

	for (const auto &cmd : cmds) {
		if (!cmd.component.empty()) {
			bv.FindComponent(cmd.component.c_str());
			bv.CenterZoomSearchResults();
			m_lastToastMessage = "IA resalto " + cmd.component + (cmd.quadrant.empty() ? "" : " [" + cmd.quadrant + "]") + ": " + cmd.message;
		} else if (!cmd.net.empty()) {
			bv.FindNet(cmd.net.c_str());
			bv.CenterZoomNet(cmd.net);
		}
	}

	if (!p_open || !(*p_open)) return;

	ImGui::SetNextWindowSize(ImVec2(DPI(460), DPI(580)), ImGuiCond_FirstUseEver);
	if (ImGui::Begin("Agente IA de Diagnostico (.NET 10)", p_open)) {
		ImGui::TextColored(m_wsConnected.load() ? ImVec4(0.13f, 0.77f, 0.37f, 1.0f) : ImVec4(0.93f, 0.26f, 0.26f, 1.0f),
		                   "%s",
		                   m_wsConnected.load() ? "● Conectado a OpenBoardView.AI (WebSocket :5080)" : "○ Reconectando a localhost:5080...");

		if (!m_lastToastMessage.empty()) {
			ImGui::Separator();
			ImGui::TextWrapped("⚡ %s", m_lastToastMessage.c_str());
		}

		ImGui::Separator();
		if (ImGui::SmallButton("Corto PPBUS_G3H")) {
			SendPromptAsync("Tengo un corto a tierra en PPBUS_G3H. Analiza por cuadrantes, dime que fusible levantar y resaltalo en OpenBoardView.");
		}
		ImGui::SameLine();
		if (ImGui::SmallButton("Analizar U7000")) {
			SendPromptAsync("Analiza el IC U7000 con el esquematico PDF e iluminalo en la placa.");
		}
		ImGui::SameLine();
		if (ImGui::SmallButton("Revisar PP3V42_G3H")) {
			SendPromptAsync("Analiza la linea PP3V42_G3H por cuadrantes e ilumina el componente clave.");
		}
		ImGui::SameLine();
		if (ImGui::SmallButton("Debugger: No Carga")) {
			SendPromptAsync("Mi laptop no carga. Inicia el debugger paso a paso como laberinto desde el MagSafe hasta la bateria.");
		}

		ImGui::Separator();
		ImGui::BeginChild("AiChatScroll", ImVec2(0, -DPI(40)), true);
		{
			std::lock_guard<std::mutex> lock(m_mutex);
			for (const auto &m : m_chatHistory) {
				if (m.role == "user") {
					ImGui::TextColored(ImVec4(0.22f, 0.74f, 0.97f, 1.0f), "Tecnico:");
				} else {
					ImGui::TextColored(ImVec4(0.13f, 0.77f, 0.37f, 1.0f), "Agente IA (.NET 10):");
				}
				ImGui::TextWrapped("%s", m.text.c_str());
				if (!m.tools.empty()) {
					std::string btnLabel = "Localizar " + m.tools + "##" + m.tools;
					if (ImGui::SmallButton(btnLabel.c_str())) {
						bv.FindComponent(m.tools.c_str());
						bv.CenterZoomSearchResults();
					}
				}
				ImGui::Separator();
			}
			if (m_queryInFlight.load()) {
				ImGui::TextColored(ImVec4(0.96f, 0.62f, 0.04f, 1.0f), "Consultando grafo BRD, PDF e IChatClient...");
			}
		}
		ImGui::EndChild();

		bool sendClicked = ImGui::InputText("##aiprompt", m_inputBuf, sizeof(m_inputBuf), ImGuiInputTextFlags_EnterReturnsTrue);
		ImGui::SameLine();
		if ((sendClicked || ImGui::Button("Preguntar")) && m_inputBuf[0] != '\0') {
			SendPromptAsync(m_inputBuf);
			m_inputBuf[0] = '\0';
		}
	}
	ImGui::End();
}

void ObvAiBridge::RenderThermalAndFlowOverlay(BoardView &bv, ImDrawList *draw) {
	if (!draw || !m_showThermalOverlay) return;
	m_animPhase = std::fmod(m_animPhase + 0.02f, 1.0f);

	std::lock_guard<std::mutex> lock(m_mutex);
	for (const auto &edge : m_activeFlowEdges) {
		ImVec2 p1 = bv.CoordToScreen(edge.fromX, edge.fromY);
		ImVec2 p2 = bv.CoordToScreen(edge.toX, edge.toY);
		ImU32 col = edge.isFault ? IM_COL32(239, 68, 68, 200) : IM_COL32(34, 197, 94, 180);
		draw->AddLine(p1, p2, col, edge.isFault ? 3.0f : 2.0f);

		for (int k = 0; k < 4; ++k) {
			float t  = std::fmod(m_animPhase + k * 0.25f, 1.0f);
			ImVec2 p = ImVec2(p1.x + (p2.x - p1.x) * t, p1.y + (p2.y - p1.y) * t);
			draw->AddCircleFilled(p, 4.5f, IM_COL32(254, 240, 138, 255));
		}
	}

	for (const auto &spot : m_activeHotspots) {
		ImVec2 center = bv.CoordToScreen(spot.x, spot.y);
		draw->AddCircleFilled(center, 36.0f, IM_COL32(239, 68, 68, 70));
		draw->AddCircleFilled(center, 20.0f, IM_COL32(249, 115, 22, 130));
		draw->AddCircleFilled(center, 9.0f, IM_COL32(254, 240, 138, 230));
		std::string label = spot.componentId + " (" + std::to_string((int)spot.tempC) + "C)";
		draw->AddText(ImVec2(center.x + 12, center.y - 8), IM_COL32(254, 240, 138, 255), label.c_str());
	}
}
