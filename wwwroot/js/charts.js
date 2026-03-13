let equityChartInstance = null;

window.renderEquityChart = function (labels, values, drawdownValues) {
    const canvas = document.getElementById('equityChart');
    if (!canvas) return;

    if (equityChartInstance) {
        equityChartInstance.destroy();
    }

    const ctx = canvas.getContext('2d');

    equityChartInstance = new Chart(ctx, {
        type: 'line',
        data: {
            labels: labels,
            datasets: [
                {
                    label: 'Portfolio-Wert',
                    data: values,
                    borderColor: '#6366f1',
                    backgroundColor: 'rgba(99, 102, 241, 0.1)',
                    fill: true,
                    tension: 0.3,
                    pointRadius: 2,
                    pointHoverRadius: 5,
                    borderWidth: 2
                },
                {
                    label: 'Drawdown',
                    data: drawdownValues,
                    borderColor: '#ef4444',
                    backgroundColor: 'rgba(239, 68, 68, 0.1)',
                    fill: true,
                    tension: 0.3,
                    pointRadius: 0,
                    borderWidth: 1,
                    yAxisID: 'drawdown'
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: {
                intersect: false,
                mode: 'index'
            },
            plugins: {
                legend: {
                    labels: {
                        color: '#71717a',
                        font: { size: 11 }
                    }
                },
                tooltip: {
                    backgroundColor: '#1a1d27',
                    borderColor: '#2a2d3a',
                    borderWidth: 1,
                    titleColor: '#e4e4e7',
                    bodyColor: '#e4e4e7',
                    callbacks: {
                        label: function (context) {
                            var val = context.parsed.y;
                            if (context.datasetIndex === 0)
                                return 'Portfolio: $' + val.toLocaleString('de-DE', { minimumFractionDigits: 2 });
                            return 'Drawdown: $' + val.toLocaleString('de-DE', { minimumFractionDigits: 2 });
                        }
                    }
                }
            },
            scales: {
                x: {
                    ticks: { color: '#71717a', font: { size: 10 } },
                    grid: { color: 'rgba(42, 45, 58, 0.5)' }
                },
                y: {
                    position: 'left',
                    ticks: {
                        color: '#71717a',
                        font: { size: 10 },
                        callback: function (val) { return '$' + val.toLocaleString(); }
                    },
                    grid: { color: 'rgba(42, 45, 58, 0.5)' }
                },
                drawdown: {
                    position: 'right',
                    ticks: {
                        color: '#ef4444',
                        font: { size: 10 },
                        callback: function (val) { return '$' + val.toLocaleString(); }
                    },
                    grid: { display: false }
                }
            }
        }
    });
};
