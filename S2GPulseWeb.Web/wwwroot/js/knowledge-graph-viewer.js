// knowledge-graph-viewer.js
// D3 v7 force-directed graph.
// Async simulation (proper D3 tick loop), zoom-to-fit fires once after layout.

window.KnowledgeGraph = (() => {
    const instances = {};

    function initGraph(containerId, graphData, selectedId, dotNetRef) {
        const container = document.getElementById(containerId);
        if (!container) return;
        destroyGraph(containerId);
        if (!graphData?.nodes?.length) return;

        if (typeof d3 === 'undefined') {
            const s = document.createElement('script');
            s.src = 'https://cdn.jsdelivr.net/npm/d3@7/dist/d3.min.js';
            s.onload = () => boot(containerId, graphData, selectedId, dotNetRef);
            document.head.appendChild(s);
        } else {
            // Small timeout so Blazor has finished painting the container
            setTimeout(() => boot(containerId, graphData, selectedId, dotNetRef), 50);
        }
    }

    function boot(containerId, graphData, selectedId, dotNetRef) {
        const container = document.getElementById(containerId);
        if (!container) return;

        const W = container.offsetWidth || 700;
        const H = container.offsetHeight || 450;

        // ── SVG ───────────────────────────────────────────────────────────────
        const svgSel = d3.select(container)
            .append('svg')
            .attr('class', 'kg-svg')
            .attr('width', '100%')
            .attr('height', '100%')
            .style('background', 'transparent');

        const mid = `kg-arrow-${containerId}`;
        svgSel.append('defs').append('marker')
            .attr('id', mid).attr('viewBox', '0 -5 10 10')
            .attr('refX', 18).attr('refY', 0)
            .attr('markerWidth', 6).attr('markerHeight', 6)
            .attr('orient', 'auto')
            .append('path').attr('d', 'M0,-5L10,0L0,5').attr('fill', '#00a8a8');

        const root = svgSel.append('g'); // zoom target

        const zoom = d3.zoom().scaleExtent([0.05, 10])
            .on('zoom', e => root.attr('transform', e.transform));
        svgSel.call(zoom);

        // ── Data ──────────────────────────────────────────────────────────────
        const nodes = graphData.nodes.map(n => ({ ...n }));
        const edges = graphData.edges.map(e => ({ ...e }));

        // ── Simulation ────────────────────────────────────────────────────────
        const sim = d3.forceSimulation(nodes)
            .alphaDecay(0.045)   // converges in ~150 ticks (~2.5s), before the 3s fallback
            .force('link', d3.forceLink(edges).id(d => d.id).distance(90))
            .force('charge', d3.forceManyBody().strength(-220))
            .force('center', d3.forceCenter(W / 2, H / 2))
            .force('collision', d3.forceCollide(32));

        // ── Links ─────────────────────────────────────────────────────────────
        const linkSel = root.append('g').selectAll('line').data(edges).join('line')
            .attr('stroke', '#00a8a855').attr('stroke-width', 1.5)
            .attr('marker-end', `url(#${mid})`);

        const labelSel = root.append('g').selectAll('text').data(edges).join('text')
            .attr('fill', '#4a7070').attr('font-size', 8).attr('text-anchor', 'middle')
            .text(d => d.relationType || '');

        // ── Nodes ─────────────────────────────────────────────────────────────
        const nodeG = root.append('g').selectAll('g').data(nodes).join('g')
            .attr('cursor', 'pointer')
            .call(d3.drag()
                .on('start', (e, d) => { if (!e.active) sim.alphaTarget(0.3).restart(); d.fx = d.x; d.fy = d.y; })
                .on('drag', (e, d) => { d.fx = e.x; d.fy = e.y; })
                .on('end', (e, d) => { if (!e.active) sim.alphaTarget(0); d.fx = null; d.fy = null; }))
            .on('click', (e, d) => {
                if (dotNetRef) dotNetRef.invokeMethodAsync('OnGraphNodeClicked', d.id);
            });

        const circles = nodeG.append('circle')
            .attr('r', d => d.id === selectedId ? 18 : 12)
            .attr('fill', d => d.id === selectedId ? '#00a8a8' : '#112e2e')
            .attr('stroke', d => d.id === selectedId ? '#00d4d4' : '#00a8a8')
            .attr('stroke-width', d => d.id === selectedId ? 3 : 1.5);

        nodeG.append('text')
            .attr('dy', '0.35em').attr('text-anchor', 'middle')
            .attr('fill', '#c8e0e0').attr('font-size', 9).attr('pointer-events', 'none')
            .text(d => d.title ? d.title.substring(0, 14) + (d.title.length > 14 ? '…' : '') : '');

        // ── Tick handler ──────────────────────────────────────────────────────
        sim.on('tick', () => {
            linkSel
                .attr('x1', d => d.source.x).attr('y1', d => d.source.y)
                .attr('x2', d => d.target.x).attr('y2', d => d.target.y);
            labelSel
                .attr('x', d => (d.source.x + d.target.x) / 2)
                .attr('y', d => (d.source.y + d.target.y) / 2);
            nodeG.attr('transform', d => `translate(${d.x},${d.y})`);
        });

        // Fit ONCE after simulation fully settles (all node positions stable).
        // Fallback at 3s in case 'end' never fires (e.g. sim still warm after drag).
        const fitOnce = (() => {
            let done = false;
            return () => { if (!done) { done = true; doFit(svgSel, zoom, nodes, container); } };
        })();
        sim.on('end', fitOnce);
        setTimeout(fitOnce, 3000);

        instances[containerId] = { sim, svgSel, zoom, nodes, circles };
    }

    // ── Zoom-to-fit ───────────────────────────────────────────────────────────
    function doFit(svgSel, zoom, nodes, container) {
        const validNodes = nodes.filter(n => n.x != null && n.y != null);
        if (!validNodes.length) return;
        const W = container.offsetWidth || 700;
        const H = container.offsetHeight || 450;
        const pad = 50;
        const minX = Math.min(...validNodes.map(n => n.x));
        const maxX = Math.max(...validNodes.map(n => n.x));
        const minY = Math.min(...validNodes.map(n => n.y));
        const maxY = Math.max(...validNodes.map(n => n.y));
        const gw = maxX - minX || 1;
        const gh = maxY - minY || 1;
        const scale = Math.min((W - pad * 2) / gw, (H - pad * 2) / gh, 3);
        const tx = W / 2 - scale * (minX + gw / 2);
        const ty = H / 2 - scale * (minY + gh / 2);
        svgSel.transition().duration(400)
            .call(zoom.transform, d3.zoomIdentity.translate(tx, ty).scale(scale));
    }

    // ── Public: re-fit on demand (⊞ button) ───────────────────────────────────
    function fitGraph(containerId) {
        const inst = instances[containerId];
        if (!inst) return;
        const c = document.getElementById(containerId);
        if (c) doFit(inst.svgSel, inst.zoom, inst.nodes, c);
    }

    // ── Public: highlight selected node (no rebuild) ──────────────────────────
    function highlightNode(containerId, selectedId) {
        const inst = instances[containerId];
        if (!inst?.circles) return;
        inst.circles
            .attr('r', d => d.id === selectedId ? 18 : 12)
            .attr('fill', d => d.id === selectedId ? '#00a8a8' : '#112e2e')
            .attr('stroke', d => d.id === selectedId ? '#00d4d4' : '#00a8a8')
            .attr('stroke-width', d => d.id === selectedId ? 3 : 1.5);
    }

    // ── Public: teardown ──────────────────────────────────────────────────────
    function destroyGraph(containerId) {
        const inst = instances[containerId];
        if (inst) { inst.sim?.stop(); delete instances[containerId]; }
        document.getElementById(containerId)
            ?.querySelectorAll('svg.kg-svg').forEach(el => el.remove());
    }

    return { initGraph, highlightNode, fitGraph, destroyGraph };
})();
