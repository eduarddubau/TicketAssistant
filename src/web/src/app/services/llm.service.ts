import { Injectable, inject, signal } from '@angular/core';
import { API_BASE } from '../config';
import { LlmInfo, OllamaComputeStatus } from '../models';
import { SessionService } from './session.service';

/**
 * Backs the header's LLM switchers: which provider/model to use and whether to force
 * Ollama onto CPU, plus the live GPU/CPU status badge. The choices ride on request headers
 * (X-Llm-Provider / X-Llm-Model / X-Ollama-Compute) that ChatClientFactory reads per request.
 */
@Injectable({ providedIn: 'root' })
export class LlmService {
  private readonly session = inject(SessionService);

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
    this.provider.set(p);
    localStorage.setItem('ta-provider', p);
    const def = this.info()?.defaultModels?.[p];
    if (def) this.setModel(def);
  }

  setModel(m: string): void {
    this.model.set(m);
    localStorage.setItem('ta-model', m);
  }

  setCompute(c: string): void {
    this.compute.set(c);
    localStorage.setItem('ta-compute', c);
    this.refreshComputeStatus();
  }
}
