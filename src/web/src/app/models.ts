// The SSE events the orchestration loop streams (mirrors the server's WriteSseAsync payloads).
export type OrchestrationEvent =
  | { type: 'assistant_text'; text: string }
  | { type: 'assistant_delta'; text: string }
  | { type: 'assistant_replace'; text: string }
  | { type: 'tool_executed'; toolName: string; succeeded: boolean }
  | { type: 'confirmation_required'; callId: string; toolName: string; arguments: Record<string, any> }
  | ServerDebugEvent;

// The loop's inner workings, streamed only when the debug console asked for them (X-Debug).
// Never shown in the transcript — ApiService routes these straight to DebugService.
export interface ServerDebugEvent {
  type: 'debug';
  stage: string;         // llm_request | llm_response | tool_call | tool_result | guardrail | …
  label: string;         // one-line headline, readable without expanding
  detail?: unknown;      // the full structured payload behind it
  elapsedMs?: number | null;
  at?: string;
}

// One line in the debug console. Client-side entries (what the browser sent, what it got back)
// and server-side ones (what the loop did) share the shape, so the panel reads as a single
// timeline of the turn rather than two half-stories.
export interface DebugEntry {
  id: number;
  at: number;                       // epoch ms, when the browser recorded it
  source: 'client' | 'server';
  stage: string;
  label: string;
  detail?: unknown;
  ms?: number | null;               // how long the step took, when that's meaningful
}

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
  ticketUrlTemplate: string | null;   // mock ticket link template (…/#{id}), null if no mock backend
  jiraEnabled: boolean;               // whether the Jira backend is active (show connect UI)
}

export interface JiraSiteInfo {
  name: string;
  siteUrl: string;
}

export interface JiraStatus {
  connected: boolean;
  accountEmail?: string | null;
  sites?: JiraSiteInfo[];
}

// A project the user can create tickets in. Provider, site (workspace) and project are kept as
// separate fields so the UI can offer them as separate choices — not every provider has sites.
export interface JiraProject {
  key: string;
  name: string;
  provider: string;              // "jira" | "mock-ticketing" | "in-memory"
  siteName?: string | null;      // workspace/site, when the provider has that concept
  siteUrl?: string | null;
}

/** Display name for a backend id, e.g. "mock-ticketing" → "Mock board". */
export function providerLabel(provider: string): string {
  switch (provider) {
    case 'jira': return 'Jira';
    case 'mock-ticketing': return 'Mock board';
    case 'in-memory': return 'In-memory board';
    default: return provider;
  }
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
