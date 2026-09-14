import hashlib, http.server, json, os, pathlib, queue, subprocess, tempfile, threading, time, uuid

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
if os.environ.get('SWITCHER_SMOKE_PAGINATED') == '1':
    records[0]['payload']['history_mode'] = 'paginated'
    records = [records[0],
        {'timestamp': STAMP, 'type': 'event_msg', 'payload': {'type': 'task_started', 'turn_id': 'synthetic-turn', 'model_context_window': 128000}},
        records[2],
        {'timestamp': STAMP, 'type': 'event_msg', 'payload': {'type': 'item_completed', 'thread_id': THREAD,
            'turn_id': 'synthetic-turn', 'item': {'type': 'UserMessage', 'id': 'synthetic-message',
                'content': [{'type': 'text', 'text': text}]}, 'completed_at_ms': 1}},
        {'timestamp': STAMP, 'type': 'event_msg', 'payload': {'type': 'task_complete', 'turn_id': 'synthetic-turn', 'last_agent_message': None}},
    ]
    for ordinal, record in enumerate(records):
        record['ordinal'] = ordinal
rollout.write_text(''.join(json.dumps(r)+'\n' for r in records), encoding='utf-8')
(ROOT/'config.toml').write_text('model_provider = "openai"\nmodel = "gpt-5.4"\nopenai_base_url = "http://127.0.0.1:1/v1"\n', encoding='utf-8')
(ROOT/'auth.json').write_text('{"OPENAI_API_KEY":"isolated-fake-key"}', encoding='utf-8')
ENV = os.environ.copy()
ENV['CODEX_HOME'] = str(ROOT)
for key in ['CODEX_SQLITE_HOME','OPENAI_API_KEY','OPENAI_BASE_URL']:
    ENV.pop(key, None)
captured_requests = queue.Queue()
if os.environ.get('SWITCHER_SMOKE_CONTEXT') == '1':
    class CaptureHandler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass
        def do_POST(self):
            body = self.rfile.read(int(self.headers.get('Content-Length', '0')))
            captured_requests.put(body)
            self.send_response(400)
            self.send_header('Content-Type', 'application/json')
            self.end_headers()
            self.wfile.write(b'{"error":{"message":"Intentional isolated test response","type":"invalid_request_error"}}')
    capture_server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), CaptureHandler)
    threading.Thread(target=capture_server.serve_forever, daemon=True).start()
    ENV['SWITCHER_SMOKE_BASE_URL'] = f'http://127.0.0.1:{capture_server.server_port}/v1'

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
    thread_ids = [THREAD]
    for _ in range(2):
        fork = server.rpc('thread/fork', {'threadId': THREAD, 'modelProvider': 'openai'})
        fork_id = fork['thread']['id']
        assert fork_id != THREAD and fork_id not in thread_ids, fork
        thread_ids.append(fork_id)
        assert text in json.dumps(fork), 'Fork lost the original message'
    print('FORKS: real Codex created two independent conversations', flush=True)

# Keep unindexed copies with the same source metadata, as in the reported bug.
copies = []
for index in range(8):
    copy = rollout.with_name(rollout.stem + f'_unused-copy-{index}.jsonl')
    copy.write_bytes(rollout.read_bytes())
    copies.append((copy, hashlib.sha256(copy.read_bytes()).hexdigest()))

for provider in ['my-provider','second-provider','openai']:
    runner = [os.environ['SWITCHER_SMOKE_EXE']] if os.environ.get('SWITCHER_SMOKE_EXE') else [
        'dotnet','run','--project',str(BASE/'smoke.csproj'),'--']
    subprocess.run(runner + [str(ROOT),provider], check=True, env=ENV)
    with Server() as server:
        threads = server.rpc('thread/list', {})['data']
        assert any(r['id'] == THREAD for r in threads), 'Original thread missing from default list'
        for thread_id in thread_ids:
            entry = next((r for r in threads if r['id']==thread_id), None)
            # Paginated forks with no own turn may be omitted from root listings;
            # read them explicitly, then require successful full-history resume.
            if entry is None:
                entry = server.rpc('thread/read', {'threadId': thread_id})['thread']
            assert entry['modelProvider'] == provider, entry
            resumed = server.rpc('thread/resume', {'threadId': thread_id})
            assert resumed['thread']['id'] == thread_id, resumed
            assert resumed['modelProvider'] == provider, resumed
            assert text in json.dumps(resumed), 'Original conversation content missing on resume'
        print(f'AFTER {provider}: original and both forks readable and resumed', flush=True)
    for copy, digest in copies:
        assert hashlib.sha256(copy.read_bytes()).hexdigest() == digest, 'Unindexed copy changed'
if os.environ.get('SWITCHER_SMOKE_CONTEXT') == '1':
    with Server() as server:
        server.rpc('thread/resume', {'threadId': thread_ids[-1]})
        server.rpc('turn/start', {'threadId': thread_ids[-1], 'input': [
            {'type': 'text', 'text': 'Synthetic follow-up for context verification.'}]})
        request = captured_requests.get(timeout=30)
        assert text.encode('utf-8') in request, 'Inherited original message missing from model request'
        print('CONTEXT_PASS: local mock received inherited original message; no real provider contacted', flush=True)
    capture_server.shutdown()
    capture_server.server_close()
print('SMOKE_PASS', ROOT, flush=True)
