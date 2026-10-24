#pragma once

#include <atomic>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

struct BoardView;

struct ObvAiCommand {
	std::string action;
	std::string component;
	std::string net;
	std::string quadrant;
	std::string message;
};

struct ObvAiFlowEdge {
	int fromX = 0;
	int fromY = 0;
	int toX   = 0;
	int toY   = 0;
	bool isFault = false;
};

struct ObvAiHotspot {
	std::string componentId;
	int x = 0;
	int y = 0;
	float tempC = 25.0f;
};

struct ObvAiChatMessage {
	std::string role;
	std::string text;
	std::string tools;
};

class ObvAiBridge {
public:
	ObvAiBridge();
	~ObvAiBridge();

	void Start(const std::string &host = "127.0.0.1", int port = 5080);
	void Stop();

	void UpdateAndRender(BoardView &bv, bool *p_open);
	void RenderThermalAndFlowOverlay(BoardView &bv, ImDrawList *draw);
	void SendPromptAsync(const std::string &prompt);
	bool IsConnected() const { return m_wsConnected.load(); }

private:
	void WebSocketThreadFunc();
	static std::string ExtractJsonString(const std::string &json, const std::string &key);

	std::string m_host = "127.0.0.1";
	int m_port         = 5080;

	std::atomic<bool> m_running{false};
	std::atomic<bool> m_wsConnected{false};
	std::atomic<bool> m_queryInFlight{false};
	std::thread m_wsThread;

	std::mutex m_mutex;
	std::vector<ObvAiCommand> m_pendingCommands;
	std::vector<ObvAiChatMessage> m_chatHistory;
	std::vector<ObvAiFlowEdge> m_activeFlowEdges;
	std::vector<ObvAiHotspot> m_activeHotspots;
	bool m_showThermalOverlay = true;
	float m_animPhase = 0.0f;
	std::string m_lastToastMessage;
	char m_inputBuf[512]{0};
};
