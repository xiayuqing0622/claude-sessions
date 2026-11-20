// Agent list rows: each subagent's row shows the model it actually runs on.
//
// The statusline always follows the lead session, so the model a subagent runs on (fixed at
// spawn, and different from the lead's under CLAUDE_CODE_SUBAGENT_MODEL, say) shows only here.
import type { AgentInfo, Register } from 'claude-code'

const ESC = '\u001b['
const paint = (code: string, text: string) => `${ESC}${code}m${text}${ESC}0m`

// claude-haiku-4-5-20251001 -> haiku-4-5 ; glm-5.3[1m] -> glm-5.3
const shortModel = (model: string) =>
  model.replace(/^claude-/, '').replace(/-\d{8}$/, '').replace(/\[1m\]$/, '')

// Anything that is not an Anthropic family (GLM, Kimi, a gateway model) is yellow, so a
// subagent that ended up on an unexpected model stands out.
const modelColor = (model: string) =>
  /opus/.test(model) ? '35'
  : /sonnet/.test(model) ? '36'
  : /haiku/.test(model) ? '32'
  : /fable/.test(model) ? '34'
  : '33'

// The engine's own row writes 35.9k; the same spelling here.
const formatTokens = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n))

const TRANSCRIPT_WINDOW_BYTES = 1024 * 1024

// Providers behind an Anthropic-compatible endpoint (GLM, for one) send zeros in the stream's
// first usage event and the real counts only in the last, so the engine's `tokenCount` of
// their subagents stays near zero. The subagent's transcript records the final usage of every
// response: the context size is the newest non-zero input + cache read + cache creation.
// Returns undefined when the transcript is missing or its last window holds no such record.
const readTranscriptContext = async (
  $: any,
  transcriptPath: string,
  id: string,
): Promise<number | undefined> => {
  const file = `${transcriptPath.replace(/\.jsonl$/, '')}/subagents/agent-${id}.jsonl`
  try {
    const { size } = await $.fs.stat(file)
    const offset = Math.max(0, size - TRANSCRIPT_WINDOW_BYTES)
    const text: string = await $.fs.read(file, { offset, length: size - offset })
    let found: number | undefined
    // The first line of the window may be cut in half; it fails to parse and is skipped.
    for (const line of text.split('\n')) {
      let record: any
      try {
        record = JSON.parse(line)
      } catch {
        continue
      }
      const usage = record?.type === 'assistant' ? record.message?.usage : undefined
      if (!usage || typeof usage !== 'object') continue
      const n =
        (usage.input_tokens ?? 0) +
        (usage.cache_read_input_tokens ?? 0) +
        (usage.cache_creation_input_tokens ?? 0)
      if (n > 0) found = n
    }
    return found
  } catch {
    return undefined
  }
}

// `[type] name · model · effort · tokens (context %) · label`, each field left out when the
// agent has none. `label` is what the agent is doing now (its latest progress summary, else
// its description); it goes last because its length varies, and the engine cuts the body at
// the row's edge.
const renderBody = (agent: AgentInfo, tokenCount: number) => {
  const model = agent.model ? shortModel(agent.model) : undefined
  const percent = agent.contextWindowSize
    ? ` (${Math.floor((tokenCount * 100) / agent.contextWindowSize)}%)`
    : ''
  const head = [agent.type && paint('2', `[${agent.type}]`), agent.name && paint('1', agent.name)]
    .filter(Boolean)
    .join(' ')
  const fields = [
    head,
    model && paint(modelColor(model), model),
    agent.effort !== undefined && paint('2', String(agent.effort)),
    paint('2', `${formatTokens(tokenCount)}${percent}`),
    agent.label.replace(/[\r\n]+/g, ' '),
  ]
  return fields.filter(Boolean).join(paint('2', ' · '))
}

export const register: Register = on => {
  on('ui.render', { component: 'AgentRow' }, async ($, e, next) => {
    const agent = e.props
    let tokenCount = agent.tokenCount
    if (agent.model && !agent.model.startsWith('claude-')) {
      const { transcriptPath } = await $.session.info()
      const fromTranscript = transcriptPath
        ? await readTranscriptContext($, transcriptPath, agent.id)
        : undefined
      if (fromTranscript !== undefined) tokenCount = fromTranscript
    }
    return next({
      ...e,
      props: { ...e.props, body: renderBody(agent, tokenCount) },
    })
  })
}
