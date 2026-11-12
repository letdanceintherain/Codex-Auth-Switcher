"""Real installed Codex, synthetic history/credentials and loopback Responses only."""
import os, pathlib, json, subprocess, time, queue

os.environ['SWITCHER_SMOKE_PAGINATED'] = '1'
os.environ['SWITCHER_SMOKE_CONTEXT'] = '1'
smoke = pathlib.Path(__file__).with_name('verify.py')
scope = {'__file__': str(smoke)}
exec(compile(smoke.read_text(encoding='utf-8').split('with Server() as server:')[0], str(smoke), 'exec'), scope)
root, thread, rollout = scope['ROOT'], scope['THREAD'], scope['rollout']
records, Server, captured = scope['records'], scope['Server'], scope['captured_requests']

def respond(self):
    request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
    captured.put(request)
    if any(item.get('type') == 'reasoning' and item.get('encrypted_content') for item in request.get('input', [])):
        self.send_response(400)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(b'{"error":{"type":"invalid_request_error","code":"invalid_encrypted_content","message":"Synthetic encrypted content item_id did not match the target item id"}}')
        return
    self.send_response(200)
    self.send_header('Content-Type', 'text/event-stream')
    self.end_headers()
    message = {'id': 'msg_synthetic_reply', 'type': 'message', 'status': 'completed', 'role': 'assistant',
               'phase': 'final_answer', 'content': [{'type': 'output_text', 'text': 'Synthetic successful continuation.', 'annotations': []}]}
    response = {'id': 'resp_synthetic', 'object': 'response', 'created_at': 1, 'model': 'gpt-5.4', 'status': 'completed',
                'output': [message], 'usage': {'input_tokens': 10, 'output_tokens': 5, 'total_tokens': 15}}
    events = [
        ('response.created', {'response': {**response, 'status': 'in_progress', 'output': []}}),
        ('response.output_item.added', {'output_index': 0, 'item': {**message, 'content': [], 'status': 'in_progress'}}),
        ('response.output_text.delta', {'output_index': 0, 'content_index': 0, 'item_id': message['id'], 'delta': 'Synthetic successful continuation.'}),
        ('response.output_item.done', {'output_index': 0, 'item': message}),
        ('response.completed', {'response': response})]
    for kind, data in events:
        self.wfile.write(('event: ' + kind + '\ndata: ' + json.dumps({'type': kind, **data}) + '\n\n').encode())
    self.wfile.flush()

scope['CaptureHandler'].do_POST = respond
(root/'config.toml').write_text('model_provider="openai"\nmodel="gpt-5.4"\nopenai_base_url=' + json.dumps(scope['ENV']['SWITCHER_SMOKE_BASE_URL']) + '\n', encoding='utf-8')
records[0]['payload']['model_provider'] = 'openai'
opaque = {'type': 'reasoning', 'id': 'rs_synthetic_relay', 'summary': [{'type':'summary_text','text':'Visible reasoning summary'}],
          'encrypted_content': 'opaque-from-relay-' * 60}
records[-1:-1] = [
    {'timestamp':scope['STAMP'], 'type':'response_item','payload':opaque},
    {'timestamp':scope['STAMP'], 'type':'response_item','payload':{'type':'function_call','call_id':'call_test','name':'exec_command','arguments':'{"cmd":"echo synthetic"}'}},
    {'timestamp':scope['STAMP'], 'type':'response_item','payload':{'type':'function_call_output','call_id':'call_test','output':'Synthetic preserved tool result'}},
    {'timestamp':scope['STAMP'], 'type':'response_item','payload':{'type':'message','role':'assistant','phase':'final_answer','content':[{'type':'output_text','text':'Synthetic preserved assistant reply'}]}}]
for i, record in enumerate(records): record['ordinal'] = i
rollout.write_text(''.join(json.dumps(r) + '\n' for r in records), encoding='utf-8')

def turn(server, id, expected_success):
    server.rpc('thread/resume', {'threadId': id})
    server.rpc('turn/start', {'threadId': id, 'input': [{'type':'text','text':'Synthetic follow-up'}]})
    request = captured.get(timeout=30)
    raw = json.dumps(request)
    for text in [scope['text'], 'call_test', 'Synthetic preserved tool result', 'Synthetic preserved assistant reply']:
        assert text in raw, ('missing context', text)
    assert ('opaque-from-relay-' not in raw) == expected_success
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        notification = server.queue.get(timeout=max(.1, deadline-time.monotonic()))
        if notification.get('method') == 'turn/completed':
            status = notification['params']['turn']['status']
            assert (status == 'completed') == expected_success, notification
            return
    raise TimeoutError('turn/completed')

def run(action):
    subprocess.run(['dotnet','run','--project',str(smoke.parent/'smoke.csproj'),'--',str(root),action], check=True, env=scope['ENV'])

try:
    with Server() as server:
        server.rpc('thread/list', {'modelProviders': []})
        fork = server.rpc('thread/fork', {'threadId': thread, 'modelProvider': 'openai'})['thread']['id']
        turn(server, thread, False)
    print('REPRODUCED: opaque reasoning rejected before repair', flush=True)
    # Same current provider: explicit repair must bypass the active-profile shortcut.
    run('--repair')
    generations = len(list((root/'sessions').rglob('*.jsonl')))
    for id in [thread, fork]:
        with Server() as server: turn(server, id, True)
    print('REPAIRED: original and real fork completed turns, including original tools/context', flush=True)
    run('--repair')
    assert len(list((root/'sessions').rglob('*.jsonl'))) == generations
    # A new foreign reasoning record must also be handled by normal switching.
    import sqlite3
    with sqlite3.connect(root/'state_5.sqlite') as db:
        current = pathlib.Path(db.execute('select rollout_path from threads where id=?',(thread,)).fetchone()[0])
    last = json.loads(current.read_bytes().splitlines()[-1])['ordinal']
    with current.open('ab') as file:
        file.write((json.dumps({'ordinal':last+1,'timestamp':scope['STAMP'],'type':'response_item','payload':opaque})+'\n').encode())
    run('relay-probe')
    with Server() as server: turn(server, thread, True)
    print('AUTOMATIC: normal provider switch detached newly generated opaque reasoning', flush=True)
finally:
    scope['capture_server'].shutdown()
    scope['capture_server'].server_close()
print('REASONING_SMOKE_PASS', root, flush=True)
