import { Injectable, inject } from '@angular/core';
import { API_BASE } from '../config';
import { ConversationInfo, OrchestrationEvent } from '../models';
import { SessionService } from './session.service';
import { LlmService } from './llm.service';
import { DebugService } from './debug.service';
import { KindsService } from './kinds.service';
import { SourcesService } from './sources.service';

/**
 * The chat API. Reads are plain JSON; the message/confirm calls return Server-Sent Events, which
 * we consume with a fetch + ReadableStream reader (EventSource can't POST) — splitting on the
 * blank-line frame boundary and parsing each `data:` line, exactly like the original console.
 *
 * It's also where the debug console taps the wire: every request and every frame is recorded, and
 * the loop's own `debug` events are pulled out of the stream here so they never reach the
 * transcript.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly session = inject(SessionService);
  private readonly llm = inject(LlmService);
  private readonly debug = inject(DebugService);
  private readonly kinds = inject(KindsService);
  private readonly sources = inject(SourcesService);

  async createConversation(): Promise<ConversationInfo> {
    const res = await fetch(`${API_BASE}/api/conversations`, {
      method: 'POST',
      headers: { ...this.session.authHeader(), ...this.debug.headers() },
    });
    const info: ConversationInfo = await res.json();

    this.debug.client('conversation', `new conversation ${info.conversationId}`, info);
    return info;
  }

  /**
   * Puts the assistant's standing instructions at the top of the trace. Called whenever the debug
   * console is opened — including mid-chat — because "what is this assistant told to be" is the
   * first thing anyone opens the panel for, and waiting for the next turn to reveal it is worse.
   */
  async traceSystemPrompt(): Promise<void> {
    if (!this.debug.enabled() || this.debug.entries().some((e) => e.stage === 'system_prompt')) return;

    try {
      const res = await fetch(`${API_BASE}/api/system-prompt`, {
        headers: { ...this.session.authHeader(), ...this.debug.headers() },
      });
      if (!res.ok) return;
      const { systemPrompt } = await res.json();
      if (systemPrompt) {
        this.debug.client('system_prompt', `system prompt · ${systemPrompt.length} chars`, { systemPrompt });
      }
    } catch {
      /* not fatal: the next model call carries the system prompt in its context anyway */
    }
  }

  sendMessage(convId: string, text: string, onEvent: (e: OrchestrationEvent) => void): Promise<void> {
    return this.stream(`${API_BASE}/api/conversations/${convId}/messages`, { text }, onEvent);
  }

  confirm(convId: string, payload: unknown, onEvent: (e: OrchestrationEvent) => void): Promise<void> {
    return this.stream(`${API_BASE}/api/conversations/${convId}/confirm`, payload, onEvent);
  }

  private async stream(url: string, body: unknown, onEvent: (e: OrchestrationEvent) => void): Promise<void> {
    const headers = {
      'Content-Type': 'application/json',
      ...this.session.authHeader(),
      ...this.llm.headers(),
      ...this.kinds.headers(),
      ...this.sources.headers(),
      ...this.debug.headers(),
    };
    const startedAt = performance.now();

    this.debug.client('http_request', `POST ${new URL(url).pathname}`, { url, headers: redact(headers), body });

    const res = await fetch(url, { method: 'POST', headers, body: JSON.stringify(body) });

    this.debug.client(
      'http_response',
      `${res.status} ${res.statusText || 'OK'} · ${res.headers.get('content-type') ?? 'no content-type'}`,
      { url, status: res.status, headers: Object.fromEntries(res.headers.entries()) },
      Math.round(performance.now() - startedAt),
    );

    if (!res.ok || !res.body) {
      throw new Error(`HTTP ${res.status} ${res.statusText}`);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    // Streamed text arrives one fragment at a time; a line each would drown the panel, so they're
    // counted here and reported as one entry when the stream ends (the loop's llm_response event
    // carries the fragments themselves).
    let deltas = 0;
    let deltaChars = 0;
    let frames = 0;

    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const chunks = buffer.split('\n\n');
      buffer = chunks.pop() ?? ''; // keep the trailing partial frame
      for (const chunk of chunks) {
        const line = chunk.split('\n').find((l) => l.startsWith('data:'));
        if (!line) continue;
        frames++;
        const event: OrchestrationEvent = JSON.parse(line.slice(5).trim());

        if (event.type === 'debug') {
          this.debug.server(event);
          continue; // the trace never reaches the chat log
        }

        if (event.type === 'assistant_delta') {
          deltas++;
          deltaChars += event.text.length;
        } else {
          this.debug.client('sse', `⇢ ${event.type}${describe(event)}`, event);
        }

        onEvent(event);
      }
    }

    if (deltas > 0) {
      this.debug.client('sse', `⇢ assistant_delta ×${deltas} · ${deltaChars} chars streamed`, { deltas, deltaChars });
    }
    this.debug.client(
      'http_response',
      `stream finished · ${frames} frame(s)`,
      { url, frames },
      Math.round(performance.now() - startedAt),
    );
  }
}

/** A short suffix for an event's headline, so the timeline reads without expanding every line. */
function describe(event: OrchestrationEvent): string {
  switch (event.type) {
    case 'tool_executed':
      return ` · ${event.toolName} ${event.succeeded ? '✓' : '✗'}`;
    case 'confirmation_required':
      return ` · ${event.toolName}`;
    case 'assistant_text':
    case 'assistant_replace':
      return ` · ${event.text.length} chars`;
    default:
      return '';
  }
}

/** Keeps the session bearer out of the trace — enough of it to recognise, not enough to reuse. */
function redact(headers: Record<string, string>): Record<string, string> {
  const copy = { ...headers };
  if (copy['Authorization']) copy['Authorization'] = `Bearer …${copy['Authorization'].slice(-6)}`;
  return copy;
}
