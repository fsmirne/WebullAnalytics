(() => {
    if (window.__sniffActive) {
      let totals = '';
      for (const [tid, bars] of window.__sniffBars) totals += `${tid}: ${bars.length}, `;
      console.log(`Sniffer already active. ${totals}Type __dumpBars() to save.`);
      return;
    }
    window.__sniffActive = true;
    window.__sniffBars = new Map();
    const seenByTicker = new Map();

    function record(json, url) {
      const m = url.match(/tickerId=(\d+)/);
      if (!m) return;
      const tid = m[1];
      if (!window.__sniffBars.has(tid)) window.__sniffBars.set(tid, []);
      if (!seenByTicker.has(tid)) seenByTicker.set(tid, new Set());
      const bars = window.__sniffBars.get(tid);
      const seen = seenByTicker.get(tid);
      const data = json?.[0]?.data ?? [];
      let added = 0;
      for (const row of data) {
        const ts = parseInt(row.split(',')[0]);
        if (Number.isFinite(ts) && !seen.has(ts)) { seen.add(ts); bars.push(row); added++; }
      }
      if (added > 0) {
        const oldest = Math.min(...seen);
        const name = tid === '913354362' ? 'SPX' : tid === '913243251' ? 'SPY' : tid;
        console.log(`${name}: +${added} bars (total ${bars.length}, oldest ${new Date(oldest*1000).toISOString().slice(0,16)}Z)`);
      }
    }

    const isTarget = u => typeof u === 'string' && u.includes('charts/query-mini');
    const origFetch = window.fetch;
    window.fetch = async function(...a) {
      const url = typeof a[0] === 'string' ? a[0] : a[0].url;
      const resp = await origFetch.apply(this, a);
      if (isTarget(url)) resp.clone().json().then(j => record(j, url)).catch(()=>{});
      return resp;
    };
    const origOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function(m, u, ...r) { this.__url = u; return origOpen.apply(this, [m, u, ...r]); };
    const origSend = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.send = function(...a) {
      if (isTarget(this.__url)) this.addEventListener('load', () => { try { record(JSON.parse(this.responseText), this.__url); } catch(e){} });
      return origSend.apply(this, a);
    };

    window.__dumpBars = () => {
      if (window.__sniffBars.size === 0) { console.warn('No bars captured.'); return; }
      for (const [tid, bars] of window.__sniffBars) {
        if (bars.length === 0) continue;
        const name = tid === '913354362' ? 'spx' : tid === '913243251' ? 'spy' : `t${tid}`;
        const blob = new Blob([bars.join('\n')], {type:'text/plain'});
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = `webull_${name}_${bars.length}.txt`;
        document.body.appendChild(a); a.click(); a.remove();
        URL.revokeObjectURL(a.href);
        console.log(`Downloaded ${a.download}`);
      }
    };

    console.log('✓ Sniffer installed. Scroll the SPX 1-min chart back as far as you can.');
    console.log('  When done: __dumpBars()');
  })();
  
  
  
  __dumpBars()