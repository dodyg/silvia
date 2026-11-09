// Stand-in for the DeepSeek chat endpoint, used to exercise the OpenAI-compatible
// wire path without a real key. It answers every chat completion with a fixed
// reply and reports how many context messages it received.
import { createServer } from 'node:http';

const port = Number(process.env.PORT ?? 8787);

createServer(async (req, res) => {
  let body = '';
  for await (const chunk of req) body += chunk;

  if (req.method !== 'POST' || !req.url?.endsWith('/chat/completions')) {
    res.writeHead(404, { 'content-type': 'application/json' }).end('{"error":"not found"}');
    return;
  }

  const request = JSON.parse(body || '{}');
  const contextMessages = (request.messages ?? []).length;
  console.log(`chat ${request.model}: ${contextMessages} messages`);

  res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({
    id: 'fake-1',
    object: 'chat.completion',
    created: Math.floor(Date.now() / 1000),
    model: request.model ?? 'fake',
    choices: [{
      index: 0,
      finish_reason: 'stop',
      message: { role: 'assistant', content: `Fake answer built from ${contextMessages} messages.` },
    }],
    usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 },
  }));
}).listen(port, () => console.log(`fake chat endpoint on http://127.0.0.1:${port}/v1`));
