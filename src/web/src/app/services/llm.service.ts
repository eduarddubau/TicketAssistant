import { Injectable, inject, signal } from '@angular/core';
import { API_BASE } from '../config';
import { LlmInfo, OllamaComputeStatus } from '../models';
import { SessionService } from './session.service';
import { DebugService } from './debug.service';

/**
 * Backs the header's LLM switchers: which provider/model to use and whether to force
 * Ollama onto CPU, plus the live GPU/CPU status badge. The choices ride on request headers
 * (X-Llm-Provider / X-Llm-Model / X-Ollama-Compute) that ChatClientFactory reads per request.
 *
 * Every switch is also written to the debug trace. Which model answered is the first thing that
 * explains a strange reply, and a switch made three turns ago is invisible in the transcript —
 * so the timeline records it, next to the turns it changes. No API keys, only which providers have
 * one configured.
 */
@Injectable({ providedIn: 'root' })
export class LlmService {
  private readonly session = inject(SessionService);
  private readonly debug = inject(DebugService);

  readonly info = signal<LlmInfo | null>(null);
  readonly ollamaModels = signal<string[]>([]);
  readonly computeStatus = signal<OllamaComputeStatus | null>(null);

  readonly provider = signal(localStorage.getItem('ta-provider') || '');
  readonly model = signal(localStorage.getItem('ta-model') || '');
  readonly compute = signal(localStorage.getItem('ta-compute') || ''); // '' = auto, 'cpu' = force CPU

  /** Per-request LLM headers for the chat/confirm calls. */
  headers(): Record<string, string> {
    const h: Record<string, string> = {};
    if (this.provider()) h['X-Llm-Provider'] = this.provider();
    if (this.model()) h['X-Llm-Model'] = this.model();
    if (this.compute()) h['X-Ollama-Compute'] = this.compute();
    return h;
  }

  async load(): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/api/llm`, { headers: this.session.authHeader() });
      const info: LlmInfo = await res.json();
      this.info.set(info);
      if (!this.provider()) this.provider.set(info.provider);
      if (!this.model()) this.model.set(info.model);

      // Which providers are actually reachable, and which one this browser will use — the trace's
      // answer to "who answered this turn, and with what key".
      const withKeys = Object.entries(info.configured ?? {}).filter(([, ok]) => ok).map(([p]) => p);
      this.debug.client(
        'llm',
        `using ${this.provider()} · ${this.model()} — connected: ${withKeys.join(', ') || 'none'}`,
        { provider: this.provider(), model: this.model(), apiDefault: { provider: info.provider, model: info.model }, providersWithCredentials: withKeys },
      );
    } catch { /* leave dropdowns empty if the API isn't up yet */ }
    await this.loadOllamaModels();
    await this.refreshComputeStatus();
  }

  async loadOllamaModels(): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/api/llm/ollama/models`);
      this.ollamaModels.set(await res.json());
    } catch { this.ollamaModels.set([]); }
  }

  async refreshComputeStatus(): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/api/llm/ollama/status`);
      this.computeStatus.set(await res.json());
    } catch { this.computeStatus.set(null); }
  }

  setProvider(p: string): void {
    const was = this.provider();
    this.provider.set(p);
    localStorage.setItem('ta-provider', p);
    const hosted = p !== 'Ollama';
    this.debug.client(
      'llm',
      `provider → ${p}${was && was !== p ? ` (was ${was})` : ''} · ${hosted ? 'hosted' : 'local, no key needed'}`,
      { provider: p, previous: was, hosted, credentialConfigured: this.info()?.configured?.[p] ?? null },
    );

    const def = this.info()?.defaultModels?.[p];
    if (def) this.setModel(def);
  }

  setModel(m: string): void {
    const was = this.model();
    this.model.set(m);
    localStorage.setItem('ta-model', m);
    if (m !== was) {
      this.debug.client('llm', `model → ${m}${was ? ` (was ${was})` : ''}`, { model: m, previous: was, provider: this.provider() });
    }
  }

  setCompute(c: string): void {
    const was = this.compute();
    this.compute.set(c);
    localStorage.setItem('ta-compute', c);
    if (c !== was) {
      this.debug.client(
        'llm',
        `compute → ${c === 'cpu' ? 'CPU (forced)' : 'GPU when the container has one'}`,
        { compute: c || 'auto', previous: was || 'auto' },
      );
    }
    this.refreshComputeStatus();
  }
}
