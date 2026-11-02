#!/usr/bin/env python3
"""Freeze public PR API metadata using inherited proxy, without supplied Authorization.

This followup capture does not import or call the candidate collector.
Selection used public API metadata before any followup candidate outputs or expected labels.
It intentionally records no labels or candidate results.
"""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parent
HEADERS = {'Accept': 'application/json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'Ashlar-public-evaluation-capture'}
KEEP_HEADERS = {'date', 'link', 'x-ratelimit-limit', 'x-ratelimit-remaining', 'x-ratelimit-reset', 'x-ratelimit-resource', 'x-github-request-id', 'retry-after', 'content-type', 'etag', 'last-modified', 'x-oauth-scopes', 'x-accepted-oauth-scopes'}
CASES = [('public-ready-01', 'cli/cli', 14716), ('public-ready-02', 'jqlang/jq', 3432), ('public-ready-03', 'jqlang/jq', 3641)]
GRAPHQL = 'query($o:String!,$r:String!,$n:Int!){repository(owner:$o,name:$r){pull_request: pullRequest(number:$n){review_decision: reviewDecision commits(last:1){nodes{commit{oid status_check_rollup: statusCheckRollup{contexts(first:100){nodes{type_name: __typename ... on CheckRun{name is_required: isRequired(pullRequestNumber:$n)} ... on StatusContext{context is_required: isRequired(pullRequestNumber:$n)}}}}}}}}}}'

def now():
    return datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z')

def pick(value, keys):
    return {key: value[key] for key in keys if key in value}

def sanitize(value, kind):
    if not isinstance(value, (dict, list)):
        return value
    if isinstance(value, dict) and 'message' in value and ('documentation_url' in value or 'status' in value):
        return value
    if kind == 'pr':
        result = pick(value, ['url', 'id', 'node_id', 'html_url', 'number', 'state', 'locked', 'title', 'created_at', 'updated_at', 'closed_at', 'merged_at', 'merge_commit_sha', 'draft', 'mergeable', 'mergeable_state', 'merged', 'rebaseable', 'author_association'])
        for key in ['head', 'base']:
            if value.get(key):
                result[key] = pick(value[key], ['label', 'ref', 'sha'])
                if value[key].get('repo'):
                    result[key]['repo'] = pick(value[key]['repo'], ['id', 'full_name', 'private', 'html_url', 'default_branch'])
        return result
    if kind == 'reviews':
        return [{**pick(item, ['id', 'node_id', 'html_url', 'state', 'commit_id', 'submitted_at', 'author_association']), 'user': pick(item.get('user') or {}, ['login'])} for item in value]
    if kind == 'checks':
        return {'total_count': value['total_count'], 'check_runs': [{**pick(item, ['id', 'node_id', 'name', 'head_sha', 'url', 'html_url', 'details_url', 'status', 'conclusion', 'started_at', 'completed_at']), 'app': pick(item.get('app') or {}, ['id', 'slug', 'name']), 'check_suite': pick(item.get('check_suite') or {}, ['id'])} for item in value['check_runs']]}
    if kind == 'statuses':
        return [pick(item, ['id', 'node_id', 'url', 'state', 'description', 'target_url', 'context', 'created_at', 'updated_at']) for item in value]
    if kind == 'combined-status':
        return {**pick(value, ['state', 'sha', 'total_count', 'url', 'commit_url']), 'statuses': sanitize(value.get('statuses', []), 'statuses')}
    if kind == 'branch':
        result = pick(value, ['name', 'protected', 'protection', 'protection_url', '_links'])
        if value.get('commit'):
            result['commit'] = pick(value['commit'], ['sha', 'url'])
        return result
    return value

def fetch(path, kind, method='GET', payload=None):
    started = now()
    headers = dict(HEADERS)
    data = None
    if payload is not None:
        headers['Content-Type'] = 'application/json'
        data = json.dumps(payload).encode()
    request = urllib.request.Request('https://api.github.com' + path, headers=headers, data=data, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    selected_headers = {key.lower(): value for key, value in response.headers.items() if key.lower() in KEEP_HEADERS}
    raw = response.read().decode()
    try:
        body = json.loads(raw)
    except json.JSONDecodeError:
        raise RuntimeError('Unexpected non-JSON response at ' + path)
    result = {'method': method, 'path': path, 'kind': kind, 'accessMode': 'inherited-proxy-no-authorization-supplied-by-code', 'startedAt': started, 'finishedAt': now(), 'observedHttpStatus': response.status, 'response': {'status': response.status, 'headers': selected_headers, 'body': sanitize(body, kind)}}
    if payload is not None:
        result['request'] = payload
    return result

def pages(path, kind):
    results = []
    next_path = path
    for unused in range(50):
        item = fetch(next_path, kind)
        results.append(item)
        link = item['response']['headers'].get('link', '')
        match = re.search(r'<https://api.github.com([^>]+)>; rel="next"', link)
        if not match or item['observedHttpStatus'] != 200:
            return results
        next_path = match.group(1)
    raise RuntimeError('Pagination exceeded50pages; retain explicit incomplete capture rather than assume complete')

def identity(body):
    return {'headSha': body['head']['sha'], 'baseSha': body['base']['sha'], 'baseRef': body['base']['ref']}

def capture(case_id, repository, number):
    case_dir = ROOT / case_id
    if case_dir.exists():
        raise RuntimeError('Refusing to overwrite frozen capture: ' + str(case_dir))
    started = now()
    prefix = '/repos/' + repository
    pr_path = prefix + '/pulls/' + str(number)
    initial = fetch(pr_path, 'pr')
    if initial['observedHttpStatus'] != 200:
        raise RuntimeError('Initial PR fetch failed: ' + pr_path)
    initial_id = identity(initial['response']['body'])
    head, base = initial_id['headSha'], urllib.parse.quote(initial_id['baseRef'], safe='')
    endpoint_groups = [
        (pr_path + '/reviews', 'reviews', True),
        (prefix + '/commits/' + head + '/check-runs', 'checks', True),
        (prefix + '/statuses/' + head + '?per_page=100', 'statuses', True),
        (prefix + '/commits/' + head + '/status', 'combined-status', True),
        (prefix + '/branches/' + base, 'branch', False),
        (prefix + '/branches/' + base + '/protection/required_status_checks', 'required-status-policy', False),
        (prefix + '/branches/' + base + '/protection/required_pull_request_reviews', 'required-review-policy', False),
        (prefix + '/rules/branches/' + base + '?per_page=100', 'active-rules', True),
    ]
    def collect(spec):
        path, kind, paginated = spec
        return pages(path, kind) if paginated else [fetch(path, kind)]
    with ThreadPoolExecutor(max_workers=4) as executor:
        groups = list(executor.map(collect, endpoint_groups))
    owner, repo = repository.split('/')
    graphql = fetch('/graphql', 'graphql-review-decision', 'POST', {'query': GRAPHQL, 'variables': {'o': owner, 'r': repo, 'n': number}})
    final_branch = fetch(prefix + '/branches/' + base, 'branch')
    final = fetch(pr_path, 'pr')
    finished = now()
    records = [initial] + [record for group in groups for record in group] + [graphql, final_branch, final]
    routes = []
    route_index = {}
    for record in records:
        key = (record['method'], record['path'])
        if key not in route_index:
            route_index[key] = len(routes)
            routes.append({'method': key[0], 'path': key[1], 'responses': []})
        routes[route_index[key]]['responses'].append(record['response'])
    final_id = identity(final['response']['body']) if final['observedHttpStatus'] == 200 else None
    auth = {'requestAuthorizationHeaderSupplied': False, 'transport': 'inherited HTTP_PROXY/HTTPS_PROXY with inherited CA trust', 'upstreamProxyIdentity': 'not observable; authenticated proxy injection is possible', 'configuredRuntimeCredentials': 'none reported by current cloud environment readiness', 'managedConnectorUsedForCaseRoutes': False, 'scopeLimit': 'Evidence establishes only visibility through this proxy during the recorded interval. Token scopes and effective repository permissions are unknown. Credential-not-supplied-to-brick is distinct from upstream anonymous authentication.'}
    raw = {'schemaVersion': '1.0', 'id': case_id, 'repository': repository, 'pullRequest': number, 'sourceUrl': 'https://github.com/' + repository + '/pull/' + str(number), 'collection': {'startedAt': started, 'finishedAt': finished, 'durationSeconds': (datetime.fromisoformat(finished.replace('Z', '+00:00')) - datetime.fromisoformat(started.replace('Z', '+00:00'))).total_seconds(), 'sequentialCaseCapture': True, 'parallelWithinCaseEndpoints': 4, 'retries': 0}, 'identity': {'initial': initial_id, 'final': final_id}, 'authorizationContext': auth, 'sanitization': {'mode': 'metadata field allowlists; retained values unchanged', 'prRetainedFields': list(initial['response']['body']), 'omitted': ['PR bodies/body_html/body_text, diffs, patches, files, code, artifacts', 'full user profiles, avatars, contact details, emails; review user.login retained', 'PR labels, assignees, milestones, requested reviewers/teams and unused repository metadata', 'review bodies and review links other than html_url', 'all check output including annotation text/summary/title; unused app/user metadata', 'branch commit message/author/committer/tree/verification and unused fields', 'status creator profiles and avatar URLs'], 'policyAndGraphQL': 'Successful policy/rules/GraphQL response JSON retained without reprojection; only query-requested GraphQL fields returned.', 'responseHeaders': 'Only date/link/rate-limit/request-id/retry-after/content-type/etag/last-modified/oauth-scope headers allowlisted; no request headers or secrets retained.'}, 'endpointInventory': [{key: record[key] for key in ['method', 'path', 'kind', 'accessMode', 'startedAt', 'finishedAt', 'observedHttpStatus']} for record in records], 'records': records}
    replay = {'id': case_id, 'kind': 'public', 'category': 'public-operational-ready-selected-before-labels', 'sourceUrl': raw['sourceUrl'], 'target': raw['sourceUrl'], 'credentialSupplied': False, 'identity': raw['identity'], 'sanitizedAuthorization': auth, 'sources': [{'uri': 'https://api.github.com' + record['path'], 'method': record['method'], 'status': record['observedHttpStatus'], 'captureFile': 'capture.json', 'recordIndex': index} for index, record in enumerate(records)], 'routes': routes, 'captureNotes': {'source': 'Captured real public GitHub API metadata; no synthetic substitutions', 'bodyProjection': 'See capture.json sanitization. Relevant API field names/shapes and retained values preserved.', 'graphql': 'Actual query/result retained as source evidence; brick is invoked without a supplied token and may skip this endpoint.', 'pagination': 'Actual Link headers followed until no rel=next; exact request paths retained. No invented pages or policy.', 'initialFinalResponses': 'Sequential repeated route responses preserve capture order; final repeats only in offline transport per runner convention.'}}
    case_dir.mkdir(parents=True)
    for filename, value in [('capture.json', raw), ('routes.json', replay)]:
        (case_dir / filename).write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n')
    hashes = {filename: hashlib.sha256((case_dir / filename).read_bytes()).hexdigest() for filename in ['capture.json', 'routes.json']}
    (case_dir / 'sha256.json').write_text(json.dumps(hashes, indent=2) + '\n')
    summary = {'id': case_id, 'repository': repository, 'pullRequest': number, 'collection': raw['collection'], 'identity': raw['identity'], 'routeCount': len(routes), 'recordCount': len(records), 'statuses': [{'kind': record['kind'], 'status': record['observedHttpStatus']} for record in records], 'files': hashes}
    print(json.dumps(summary), flush=True)

if __name__ == '__main__':
    selected = sys.argv[1:] or [case[0] for case in CASES]
    for item in CASES:
        if item[0] in selected:
            capture(*item)
