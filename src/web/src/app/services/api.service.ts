import { Injectable, inject } from '@angular/core';
import { API_BASE } from '../config';
import { ConversationInfo, OrchestrationEvent } from '../models';
import { SessionService } from './session.service';
import { LlmService } from './llm.service';

/**
 * The chat API. Reads are plain JSON; the message/confirm calls return Server-Sent Events, which
 * we consume with a fetch + ReadableStream reader (EventSource can't POST) — splitting on the
 * blank-line frame boundary and parsing each `data:` line, exactly like the original console.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly session = inject(SessionService);
  private readonly llm = inject(LlmService);

  async createConversation(): Promise<ConversationInfo> {
    const res = await fetch(`${API_BASE}/api/conversations`, {
      method: 'POST',
      headers: this.session.authHeader(),
    });
    return res.json();
  }

  sendMessage(convId: string, text: string, onEvent: (e: OrchestrationEvent) => void): Promise<void> {
    return this.stream(`${API_BASE}/api/conversations/${convId}/messages`, { text }, onEvent);
  }

  confirm(convId: string, payload: unknown, onEvent: (e: OrchestrationEvent) => void): Promise<void> {
    return this.stream(`${API_BASE}/api/conversations/${convId}/confirm`, payload, onEvent);
  }

  private async stream(url: string, body: unknown, onEvent: (e: OrchestrationEvent) => void): Promise<void> {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...this.session.authHeader(), ...this.llm.headers() },
      body: JSON.stringify(body),
    });
    if (!res.ok || !res.body) {
      throw new Error(`HTTP ${res.status} ${res.statusText}`);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });
      const chunks = buffer.split('\n\n');
      buffer = chunks.pop() ?? ''; // keep the trailing partial frame
      for (const chunk of chunks) {
        const line = chunk.split('\n').find((l) => l.startsWith('data:'));
        if (line) onEvent(JSON.parse(line.slice(5).trim()));
      }
    }
  }
}
