// The SSE events the orchestration loop streams (mirrors the server's WriteSseAsync payloads).
export type OrchestrationEvent =
  | { type: 'assistant_text'; text: string }
  | { type: 'assistant_delta'; text: string }
  | { type: 'assistant_replace'; text: string }
  | { type: 'tool_executed'; toolName: string; succeeded: boolean }
  | { type: 'confirmation_required'; callId: string; toolName: string; arguments: Record<string, any> };

// A rendered line in the chat log.
export interface ChatMessage {
  role: 'user' | 'assistant' | 'tool' | 'error';
  text: string;       // raw text (streamed into for assistant messages)
  html?: string;      // rendered markdown, set once a message is complete
  streaming?: boolean;
}

// What the server tells the client when a new conversation starts.
export interface ConversationInfo {
  conversationId: string;
  greeting: string;
  boardUrl: string;
  ticketUrlTemplate: string | null;
  ticketBackend: string;   // "Http" | "Jira" | "InMemory"
}

export interface JiraStatus {
  connected: boolean;
  siteUrl?: string | null;
  accountEmail?: string | null;
}

export interface LlmInfo {
  providers: string[];
  defaultModels: Record<string, string>;
  configured: Record<string, boolean>;
  provider: string;
  model: string;
}

export interface OllamaComputeStatus {
  loaded: boolean;
  model: string | null;
  processor: string | null;
  gpuAttached: boolean;
  hostHasGpu: boolean;
}
