(function () {
  'use strict';
  const byId = (id) => document.getElementById(id);
  const runtime = () => window.KeyboundOverlayRuntime;
  let state = null;
  let toggleBusy = false;
  let flowStopBusy = false;
  let flowStopRequestedAt = 0;
  let serviceConnected = false;

  function render(next) {
    if (!next || next.Version !== 1) return;
    state = next;
    const connected = Boolean(next.NetworkConnected);
    document.body.classList.toggle('online', connected);
    byId('network-state').textContent = connected ? '已连接' : '离线';
    byId('target-count').textContent = String(next.TargetCount || 0);
    byId('attempt-state').textContent = next.AttemptState || '等待题目';
    byId('entity-id').textContent = next.EntityId || '未选择怪物';
    byId('target-text').textContent = next.TargetText || '等待题目';
    byId('automation-status').textContent = next.AutomationStatus || next.MemoryStatus || '等待状态';
    byId('input-state').textContent = !next.InputVisible ? '不可见' : (next.InputFocused ? '已聚焦' : '未聚焦');
    byId('input-state').title = next.InputDiagnostic || '';
    byId('input-text').textContent = next.InputText || '—';
    byId('budget').textContent = `${next.BudgetMilliseconds || 1500} ms`;
    byId('event-time').textContent = next.LastEventTime || '--:--:--';
    byId('last-event').textContent = next.LastEvent || '等待事件';
    byId('event-detail').textContent = next.LastEventDetail || next.MemoryStatus || '—';
    byId('score').textContent = next.Score || '分数：—';
    byId('phase').textContent = next.Phase || '阶段：—';

    const toggle = byId('automation-toggle');
    toggle.setAttribute('aria-pressed', next.AutomationEnabled ? 'true' : 'false');
    toggle.disabled = toggleBusy || !next.Ready;
    byId('toggle-label').textContent = next.AutomationEnabled ? '自动攻击开启' : '自动攻击关闭';
    if (!next.FlowActive) flowStopBusy = false;
    const completed = Math.max(0, Number(next.FlowCompletedRuns) || 0);
    const total = Math.max(0, Number(next.FlowTargetRuns) || 0);
    byId('flow-progress').textContent = total ? `连续流程（测试）：已完成 ${completed}/${total} 局` : '连续流程（测试）未启动';
    byId('flow-status').textContent = flowStopBusy ? '正在中断，等待 ACT 确认' :
      (next.FlowStatus || '在 ACT 中设置局数并启动');
    byId('flow-status').title = next.FlowStatus || '';
    byId('flow-stop').disabled = flowStopBusy || !next.Ready || !next.FlowActive || !serviceConnected;
    byId('flow-stop').textContent = flowStopBusy ? '中断中…' : '中断流程';
  }

  async function pollState() {
    const config = runtime();
    if (!config) return;
    try {
      const response = await fetch(`${config.endpoint}/state?token=${encodeURIComponent(config.token)}&t=${Date.now()}`, {
        cache: 'no-store',
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      serviceConnected = true;
      if (flowStopBusy && Date.now() - flowStopRequestedAt > 5000) flowStopBusy = false;
      render(await response.json());
    } catch (_) {
      serviceConnected = false;
      document.body.classList.remove('online');
      byId('automation-toggle').disabled = true;
      byId('flow-stop').disabled = true;
      byId('flow-status').textContent = 'ACT 连接中断，请在 ACT 页面停止流程';
    }
  }

  function refreshRuntime() {
    const script = document.createElement('script');
    script.src = `runtime.js?t=${Date.now()}`;
    script.onload = script.onerror = () => script.remove();
    document.head.appendChild(script);
  }

  byId('automation-toggle').addEventListener('click', async () => {
    const config = runtime();
    if (!config || !state || toggleBusy) return;
    toggleBusy = true;
    render(state);
    try {
      const enabled = !state.AutomationEnabled;
      const response = await fetch(`${config.endpoint}/automation?enabled=${enabled}&token=${encodeURIComponent(config.token)}`, {
        method: 'POST',
        cache: 'no-store',
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      await pollState();
    } catch (_) {
      serviceConnected = false;
      byId('flow-status').textContent = '控制请求失败，请在 ACT 页面停止流程';
    } finally {
      toggleBusy = false;
      if (state && serviceConnected) render(state);
    }
  });

  byId('flow-stop').addEventListener('click', async () => {
    const config = runtime();
    if (!config || !state || !state.FlowActive || flowStopBusy || !serviceConnected) return;
    flowStopBusy = true;
    flowStopRequestedAt = Date.now();
    render(state);
    try {
      const response = await fetch(`${config.endpoint}/flow/stop?token=${encodeURIComponent(config.token)}`, {
        method: 'POST', cache: 'no-store',
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      await pollState();
    } catch (_) {
      flowStopBusy = false;
      byId('flow-stop').disabled = !serviceConnected;
      byId('flow-stop').textContent = '重试中断';
      byId('flow-status').textContent = '中断请求失败，请重试或在 ACT 页面停止';
    }
  });

  const preview = new URLSearchParams(location.search).get('preview') === '1';
  if (preview) {
    serviceConnected = true;
    render({
      Version: 1, Ready: true, NetworkConnected: true, TargetCount: 5,
      InputVisible: true, InputFocused: true, InputText: '单手是枪的男人',
      AutomationEnabled: true, AutomationStatus: '自动输入：已回车提交，等待攻击确认',
      FlowActive: true, FlowCompletedRuns: 3, FlowTargetRuns: 10, FlowStage: 'Combat',
      FlowStatus: '第 4 局，自动答题中',
      AttemptState: '等待攻击确认', EntityId: '0x40005C64', TargetText: '单手是枪的男人',
      BudgetMilliseconds: 1500, Score: '分数：10300', Phase: '阶段：0x2',
      LastEvent: 'SUBMIT', LastEventDetail: 'waiting-confirmation; remainingMs=184',
      LastEventTime: '02:48:16.204',
    });
  } else {
    pollState();
    setInterval(pollState, 350);
    setInterval(refreshRuntime, 2000);
  }
})();
