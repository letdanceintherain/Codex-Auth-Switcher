import json, os, pathlib, queue, subprocess, tempfile, threading, time, uuid

BASE = pathlib.Path(__file__).parent
ROOT = pathlib.Path(tempfile.mkdtemp(prefix='switcher-smoke-'))
CLI = os.environ['SWITCHER_SMOKE_CLI']
THREAD = str(uuid.uuid4())
STAMP = '2026-09-14T12:00:00Z'
text = 'Preserved local conversation: hello from before switching.'
rollout = ROOT/'sessions/2026/09/14'/f'rollout-2026-09-14T12-00-00-{THREAD}.jsonl'
rollout.parent.mkdir(parents=True)
records = [
    {'timestamp': STAMP, 'type': 'session_meta', 'payload': {'id': THREAD, 'timestamp': STAMP, 'cwd': str(ROOT),
        'originator': 'codex_cli_rs', 'cli_version': '0.118.0', 'source': 'cli', 'model_provider': 'crs'}},
    {'timestamp': STAMP, 'type': 'event_msg', 'payload': {'type': 'user_message', 'message': text, 'images': [], 'local_images': []}},
    {'timestamp': STAMP, 'type': 'response_item', 'payload': {'type': 'message', 'role': 'user', 'content': [{'type': 'input_text', 'text': text}]}}
]
rollout.write_text(''.join(json.dumps(r)+'\n' for r in records), encoding='utf-8')
(ROOT/'config.toml').write_text('model_provider = "openai"\nmodel = "gpt-5.4"\nopenai_base_url = "http://127.0.0.1:1/v1"\n', encoding='utf-8')
(ROOT/'auth.json').write_text('{"OPENAI_API_KEY":"isolated-fake-key"}', encoding='utf-8')
ENV = os.environ.copy()
ENV['CODEX_HOME'] = str(ROOT)
for key in ['CODEX_SQLITE_HOME','OPENAI_API_KEY','OPENAI_BASE_URL']:
    ENV.pop(key, None)

class Server:
    def __enter__(self):
        self.log = (ROOT/f'server-{time.time_ns()}.log').open('w', encoding='utf-8')
        self.proc = subprocess.Popen([CLI, 'app-server'], cwd=ROOT, env=ENV, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=self.log, text=True, encoding='utf-8', creationflags=subprocess.CREATE_NO_WINDOW)
        self.queue = queue.Queue()
        def read():
            for line in self.proc.stdout:
                self.queue.put(json.loads(line))
        threading.Thread(target=read, daemon=True).start()
        self.serial = 0
        self.rpc('initialize', {'clientInfo': {'name': 'switcher_isolated_test', 'version':'1.0'}, 'capabilities': {'experimentalApi': True}})
        self.send({'method':'initialized'})
        return self
    def send(self, value):
        self.proc.stdin.write(json.dumps(value)+'\n')
        self.proc.stdin.flush()
    def rpc(self, method, params):
        self.serial += 1
        serial = self.serial
        self.send({'id':serial,'method':method,'params':params})
        deadline = time.monotonic()+35
        while time.monotonic()<deadline:
            response = self.queue.get(timeout=max(0.1,deadline-time.monotonic()))
            if response.get('id') == serial:
                if 'error' in response:
                    raise RuntimeError((method,response['error']))
                return response['result']
        raise TimeoutError(method)
    def __exit__(self, *args):
        self.proc.stdin.close()
        try: self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait()
        self.log.close()

with Server() as server:
    all_threads = server.rpc('thread/list', {'modelProviders':[]})['data']
    assert THREAD in [r['id'] for r in all_threads], all_threads
    default_threads = server.rpc('thread/list', {})['data']
    assert THREAD not in [r['id'] for r in default_threads], 'Reproduction did not reproduce provider filtering'
    print('BEFORE: thread exists but is hidden under openai', flush=True)

for provider in ['my-provider','second-provider','openai']:
    runner = [os.environ['SWITCHER_SMOKE_EXE']] if os.environ.get('SWITCHER_SMOKE_EXE') else [
        'dotnet','run','--project',str(BASE/'smoke.csproj'),'--']
    subprocess.run(runner + [str(ROOT),provider], check=True, env=ENV)
    with Server() as server:
        threads = server.rpc('thread/list', {})['data']
        entry = next((r for r in threads if r['id']==THREAD), None)
        assert entry is not None, threads
        assert entry['modelProvider'] == provider, entry
        resumed = server.rpc('thread/resume', {'threadId': THREAD})
        assert resumed['thread']['id'] == THREAD, resumed
        assert resumed['modelProvider'] == provider, resumed
        assert text in json.dumps(resumed), 'Original conversation content missing on resume'
        print(f'AFTER {provider}: same thread visible and resumed with original message', flush=True)
print('SMOKE_PASS', ROOT, flush=True)
