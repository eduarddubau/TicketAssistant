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
/**
 * What each model is actually like to use here, from replaying the same reads against the seeded
 * board rather than from its parameter count. Keyed by the exact tag, because that is what the
 * picker offers; a model someone adds to OLLAMA_MODELS gets no note, which is honest — nobody has
 * run it against this app.
 *
 * The hosted providers are deliberately absent: their lineups change under us, and a stale note
 * about a model that has since been retrained is worse than none.
 */
const MODEL_NOTES: Record<string, string> = {
  'qwen2.5:3b': 'fast, and gets listings right — the default',
  'qwen3:4b-instruct': 'steadiest tool caller; bigger, so slower to load',
  'qwen2.5:1.5b': 'fastest, but often answers without reading — for testing',
};

@Injectable({ providedIn: 'root' })
export class LlmService {
  private readonly session = inject(SessionService);
  private readonly debug = inject(DebugService);

  readonly info = signal<LlmInfo | null>(null);
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
    await this.refreshComputeStatus();
  }

  /**
   * The models the current provider is configured with — the picker's options. Configuration rather
   * than "everything the provider could serve": for Ollama these are the ones the stack downloaded,
   * so picking one can't leave the assistant waiting on a model that isn't there.
   */
  modelsFor(provider = this.provider()): string[] {
    return this.info()?.models?.[provider] ?? [];
  }

  /**
   * The same list with the one-line note the picker shows beside each name. The difference between
   * these models is not the size in their name — it is whether an answer can be trusted, and that
   * is invisible until a listing comes back three items short. So the notes say what was actually
   * measured on the seeded board (see MODEL_NOTES); anything not on that list is offered without a
   * note rather than with a guess.
   */
  modelOptions(provider = this.provider()): { id: string; note?: string }[] {
    return this.modelsFor(provider).map((id) => ({ id, note: MODEL_NOTES[id] }));
  }

  /** The note for whatever is selected — the picker's tooltip. */
  noteFor(model = this.model()): string | undefined {
    return MODEL_NOTES[model];
  }

  async refreshComputeStatus(): Promise<void> {
    try {
      const res = await fetch(`${API_BASE}/api/llm/ollama/status`);
      this.computeStatus.set(await res.json());
    } catch { this.computeStatus.set(null); }
  }

  setProvider(p: string): void {
    const was = this.provider();
    // Whatever the new provider's list starts with; setModel below applies it.
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
