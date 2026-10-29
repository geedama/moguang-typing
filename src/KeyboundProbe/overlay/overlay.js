(function () {
  'use strict';
  const byId = (id) => document.getElementById(id);
  const runtime = () => window.KeyboundOverlayRuntime;
  let state = null;
  let toggleBusy = false;

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
  }

  async function pollState() {
    const config = runtime();
    if (!config) return;
    try {
      const response = await fetch(`${config.endpoint}/state?token=${encodeURIComponent(config.token)}&t=${Date.now()}`, {
        cache: 'no-store',
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      render(await response.json());
    } catch (_) {
      document.body.classList.remove('online');
      byId('automation-toggle').disabled = true;
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
    } finally {
      toggleBusy = false;
      if (state) render(state);
    }
  });

  const preview = new URLSearchParams(location.search).get('preview') === '1';
  if (preview) {
    render({
      Version: 1, Ready: true, NetworkConnected: true, TargetCount: 5,
      InputVisible: true, InputFocused: true, InputText: '单手是枪的男人',
      AutomationEnabled: true, AutomationStatus: '自动输入：已回车提交，等待攻击确认',
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
