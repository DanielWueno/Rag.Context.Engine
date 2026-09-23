#!/usr/bin/env python3
"""
comparar-capacidad.py -- Ordena N maquinas a partir de los registros JSON
que producen capacidad-ia.sh y capacidad-ia.ps1.

    python3 comparar-capacidad.py maquinas/*.json
    python3 comparar-capacidad.py maquinas/*.json --out informe.txt
    python3 comparar-capacidad.py maquinas/*.json --html informe.html

La metrica de orden es el ANCHO DE BANDA EFECTIVO (GB/s) = tamano_modelo x tok/s.
Es independiente del modelo que haya corrido cada maquina, y por eso se pueden
comparar equipos que ni siquiera tienen los mismos modelos instalados.

Si una maquina no tiene Ollama, se usa su indice sintetico de memoria y se
proyecta con la razon observada en las maquinas que si tienen ambas medidas.
Esos valores salen marcados con ~ porque son estimacion, no medicion.
"""
import glob
import json
import os
import sys

SCHEMA = "capacidad-ia/1"

_LINEAS = []


def emit(s=""):
    """Imprime y acumula, para poder volcar lo mismo a un archivo."""
    print(s)
    _LINEAS.append(s)


def esc(s):
    return (str(s).replace("&", "&amp;").replace("<", "&lt;")
            .replace(">", "&gt;").replace('"', "&quot;"))


def cargar(rutas):
    regs = []
    for r in rutas:
        try:
            with open(r, encoding="utf-8-sig") as f:
                d = json.load(f)
        except Exception as e:
            print("  aviso: no se pudo leer %s (%s)" % (r, e), file=sys.stderr)
            continue
        if d.get("schema") != SCHEMA:
            print("  aviso: %s no es esquema %s, se omite" % (r, SCHEMA), file=sys.stderr)
            continue
        d["_archivo"] = r
        regs.append(d)
    return regs


def g(d, *ks):
    """Acceso anidado tolerante a claves ausentes."""
    for k in ks:
        if not isinstance(d, dict):
            return None
        d = d.get(k)
    return d


def calibracion(regs):
    """Razon efectivo/sintetico en las maquinas que tienen las dos medidas."""
    razones = []
    for d in regs:
        ef = g(d, "inference", "effective_bandwidth_gbs") or 0
        sy = g(d, "synthetic", "memcpy_gbs") or 0
        if ef > 0 and sy > 0:
            razones.append(ef / sy)
    if not razones:
        return None
    razones.sort()
    return razones[len(razones) // 2]   # mediana


def puntaje(d, ratio):
    """(valor GB/s, medido?) para ordenar."""
    ef = g(d, "inference", "effective_bandwidth_gbs") or 0
    if ef > 0:
        return ef, True
    sy = g(d, "synthetic", "memcpy_gbs") or 0
    if sy > 0 and ratio:
        return sy * ratio, False
    return 0.0, False


def avisos(d):
    out = []
    if g(d, "power", "battery_present"):
        out.append("laptop: throttling termico en cargas largas")
    ven = g(d, "gpu", "vendor")
    proc = (g(d, "inference", "processor") or "")
    if "CPU" in proc.upper():
        out.append("ATENCION: corrio en CPU, no en GPU")
    if ven == "intel":
        out.append("iGPU Intel: Ollama no la usa por defecto")
    elif ven == "ninguno":
        out.append("sin GPU util")
    if g(d, "accelerator", "name"):
        out.append("tiene NPU/ANE, pero Ollama no lo usa")
    if not g(d, "inference", "ollama_present"):
        out.append("sin Ollama: valor proyectado, no medido")
    ef = g(d, "inference", "efficiency_pct") or 0
    if ef and ef < 50:
        out.append("eficiencia %d%%: la maquina esta desaprovechada" % ef)
    return out



CSS = """
:root{--bg:#fbfbfa;--fg:#1a1a19;--mut:#6b6b66;--line:#e3e3df;--card:#fff;
--ok:#1a7f4b;--warn:#a35a00;--bad:#b3261e;--bar:#2f6f9f;--barbg:#e8e8e4}
@media (prefers-color-scheme:dark){:root{--bg:#16161a;--fg:#e9e9e6;--mut:#9b9b95;
--line:#2c2c31;--card:#1e1e23;--ok:#4ac585;--warn:#e0a458;--bad:#f28b82;
--bar:#6aa9d8;--barbg:#2c2c31}}
*{box-sizing:border-box}
body{margin:0;padding:2rem 1.25rem;background:var(--bg);color:var(--fg);
font:15px/1.55 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif}
.wrap{max-width:1000px;margin:0 auto}
h1{font-size:1.5rem;margin:0 0 .25rem}
.sub{color:var(--mut);font-size:.85rem;margin-bottom:1.75rem}
h2{font-size:1.05rem;margin:2.25rem 0 .75rem;padding-bottom:.4rem;
border-bottom:1px solid var(--line)}
.scroll{overflow-x:auto}
table{border-collapse:collapse;width:100%;font-size:.88rem}
th{text-align:left;font-weight:600;color:var(--mut);font-size:.72rem;
letter-spacing:.06em;text-transform:uppercase;padding:.5rem .6rem;
border-bottom:1px solid var(--line);white-space:nowrap}
td{padding:.6rem;border-bottom:1px solid var(--line);vertical-align:middle}
tr:last-child td{border-bottom:none}
.num{font-variant-numeric:tabular-nums;text-align:right;white-space:nowrap}
.big{font-weight:650;font-size:1rem}
.est{color:var(--mut);font-style:italic}
.barwrap{background:var(--barbg);border-radius:3px;height:8px;width:110px;
overflow:hidden;display:inline-block;vertical-align:middle}
.bar{background:var(--bar);height:100%}
.card{background:var(--card);border:1px solid var(--line);border-radius:8px;
padding:1rem 1.1rem;margin-bottom:.9rem}
.card h3{margin:0 0 .6rem;font-size:1rem}
.rank{display:inline-block;background:var(--barbg);color:var(--mut);
border-radius:4px;padding:.05rem .45rem;margin-right:.5rem;font-size:.8rem}
dl{display:grid;grid-template-columns:auto 1fr;gap:.3rem 1rem;margin:0;font-size:.87rem}
dt{color:var(--mut)}
dd{margin:0}
.flag{display:block;font-size:.85rem;margin-top:.5rem;padding-left:1.4rem;
text-indent:-1.4rem}
.f-bad{color:var(--bad)} .f-warn{color:var(--warn)} .f-mut{color:var(--mut)}
.note{color:var(--mut);font-size:.83rem;line-height:1.6;
border-left:2px solid var(--line);padding-left:.9rem;margin-top:1rem}
@media print{body{padding:0;background:#fff}.card{break-inside:avoid}}
"""


def _clase_aviso(a):
    if a.startswith("ATENCION") or a.startswith("sin GPU"):
        return "f-bad"
    if a.startswith("sin Ollama"):
        return "f-mut"
    return "f-warn"


def render_html(regs, ratio, mejor):
    """Informe autocontenido: sin CDN, imprimible, claro y oscuro."""
    import datetime
    o = []
    o.append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\">")
    o.append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
    o.append("<title>Capacidad de inferencia</title><style>%s</style></head><body><div class=\"wrap\">" % CSS)
    o.append("<h1>Comparacion de capacidad de inferencia</h1>")
    cal = ("Calibracion efectivo/sintetico: %.2f" % ratio) if ratio else "Sin maquinas con Ollama: solo indices sinteticos"
    o.append("<p class=\"sub\">%d maquinas &middot; %s &middot; generado %s</p>" % (
        len(regs), esc(cal),
        datetime.datetime.now().strftime("%Y-%m-%d %H:%M")))

    o.append("<h2>Ranking</h2><div class=\"scroll\"><table><thead><tr>")
    for h in ["#", "Equipo", "SO", "GPU", "GB/s efectivo", "tok/s", "Eficiencia", "Relativo"]:
        o.append("<th>%s</th>" % h)
    o.append("</tr></thead><tbody>")
    for i, d in enumerate(regs, 1):
        val = d["_val"]
        rel = (val / mejor * 100) if mejor else 0
        gbs = "%.0f" % val
        if not d["_medido"]:
            gbs = "<span class=\"est\">~%s</span>" % gbs
        tps = g(d, "inference", "generation_tps") or 0
        efi = g(d, "inference", "efficiency_pct") or 0
        o.append("<tr>")
        o.append("<td class=\"num\">%d</td>" % i)
        o.append("<td>%s</td>" % esc(g(d, "host", "name") or "?"))
        o.append("<td>%s</td>" % esc(g(d, "host", "os") or "?"))
        o.append("<td>%s</td>" % esc(g(d, "gpu", "name") or "-"))
        o.append("<td class=\"num big\">%s</td>" % gbs)
        o.append("<td class=\"num\">%s</td>" % (("%.1f" % tps) if tps else "-"))
        o.append("<td class=\"num\">%s</td>" % (("%d%%" % efi) if efi else "-"))
        o.append("<td class=\"num\"><span class=\"barwrap\"><span class=\"bar\" style=\"width:%.0f%%\"></span></span> %.0f%%</td>" % (rel, rel))
        o.append("</tr>")
    o.append("</tbody></table></div>")
    o.append("<p class=\"note\">GB/s efectivo = tamano del modelo x tokens/s. Es independiente "
             "del modelo que corrio cada maquina, por eso se pueden comparar equipos que ni "
             "siquiera tienen los mismos modelos instalados. Los valores con <span class=\"est\">~</span> "
             "son proyectados desde el indice sintetico, no medidos.</p>")

    o.append("<h2>Detalle por equipo</h2>")
    for i, d in enumerate(regs, 1):
        o.append("<div class=\"card\"><h3><span class=\"rank\">%d</span>%s</h3><dl>" % (
            i, esc(g(d, "host", "name") or "?")))
        bw = g(d, "memory", "bandwidth_theoretical_gbs")
        filas = [
            ("CPU", "%s (%s nucleos logicos)" % (g(d, "cpu", "model") or "?", g(d, "cpu", "cores_logical") or "?")),
            ("RAM", "%s GB" % (g(d, "memory", "total_gb") or "?")),
            ("Bus teorico", ("%s GB/s" % bw) if bw else "no legible"),
        ]
        mod = g(d, "inference", "model")
        if mod:
            filas.append(("Medicion", "%s (%s GB) &rarr; %s tok/s en %s" % (
                esc(mod), g(d, "inference", "model_size_gb"),
                g(d, "inference", "generation_tps"),
                esc(g(d, "inference", "processor") or "?"))))
        filas.append(("Sintetico", "%s GB/s (%s)" % (
            g(d, "synthetic", "memcpy_gbs"), esc(g(d, "synthetic", "method") or "-"))))
        for k, v in filas:
            o.append("<dt>%s</dt><dd>%s</dd>" % (k, v))
        o.append("</dl>")
        for a in avisos(d):
            o.append("<span class=\"flag %s\">&#9888; %s</span>" % (_clase_aviso(a), esc(a)))
        o.append("</div>")

    o.append("<h2>Sobre el indice sintetico</h2>")
    o.append("<p class=\"note\">Es una copia de memoria de un solo hilo. Entre la implementacion "
             "de Python (Linux/macOS) y la de .NET (Windows) hay hasta 15% de diferencia medida "
             "sobre el mismo equipo, asi que sirve para ordenar por categoria, no para distinguir "
             "dos maquinas parecidas. Cuando hay Ollama manda el ancho de banda efectivo, que si "
             "concuerda entre ambas implementaciones (0.04% de diferencia medida).<br><br>"
             "Ni el NPU de Intel ni el Neural Engine de Apple participan: Ollama y llama.cpp no "
             "tienen backend de NPU. Sus backends son Metal, CUDA, ROCm, Vulkan y CPU.</p>")
    o.append("</div></body></html>")
    return "".join(o)


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 2
    txt_out = None
    html_out = None
    rutas = []
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--out" and i + 1 < len(args):
            txt_out = args[i + 1]; i += 2; continue
        if a == "--html" and i + 1 < len(args):
            html_out = args[i + 1]; i += 2; continue
        exp = glob.glob(a)
        rutas.extend(exp if exp else [a])
        i += 1

    regs = cargar(rutas)
    if not regs:
        print("No se leyo ningun registro valido.", file=sys.stderr)
        return 1

    ratio = calibracion(regs)
    for d in regs:
        d["_val"], d["_medido"] = puntaje(d, ratio)
    regs.sort(key=lambda x: x["_val"], reverse=True)
    mejor = regs[0]["_val"] or 1.0

    emit("")
    emit("COMPARACION DE CAPACIDAD DE INFERENCIA   (%d maquinas)" % len(regs))
    emit("=" * 100)
    if ratio:
        emit("Calibracion efectivo/sintetico: %.2f  (mediana de las maquinas con ambas medidas)" % ratio)
    else:
        emit("Sin ninguna maquina con Ollama: solo se comparan indices sinteticos.")
    emit("")

    hdr = "%-3s %-18s %-9s %-22s %8s %8s %7s %6s" % (
        "#", "EQUIPO", "SO", "GPU", "GB/s", "tok/s", "EFIC", "REL")
    emit(hdr)
    emit("-" * 100)
    for i, d in enumerate(regs, 1):
        nom = (g(d, "host", "name") or "?")[:18]
        so = (g(d, "host", "os") or "?")[:9]
        gpu = (g(d, "gpu", "name") or "-")[:22]
        val = d["_val"]
        marca = "" if d["_medido"] else "~"
        tps = g(d, "inference", "generation_tps") or 0
        efi = g(d, "inference", "efficiency_pct") or 0
        rel = val / mejor if mejor else 0
        emit("%-3d %-18s %-9s %-22s %8s %8s %7s %5.0f%%" % (
            i, nom, so, gpu,
            "%s%.0f" % (marca, val),
            ("%.1f" % tps) if tps else "-",
            ("%d%%" % efi) if efi else "-",
            rel * 100))
    emit("-" * 100)
    emit("GB/s = ancho de banda efectivo. '~' = proyectado desde el indice sintetico.")
    emit("REL  = rendimiento relativo a la maquina lider.")

    emit("")
    emit("DETALLE Y ADVERTENCIAS")
    emit("=" * 100)
    for i, d in enumerate(regs, 1):
        emit("")
        emit("%d. %s  (%s)" % (i, g(d, "host", "name") or "?",
                                  os.path.basename(d["_archivo"])))
        emit("   CPU     : %s  (%s nucleos logicos)" % (
            g(d, "cpu", "model") or "?", g(d, "cpu", "cores_logical") or "?"))
        bw = g(d, "memory", "bandwidth_theoretical_gbs")
        bw_txt = ("%s GB/s" % bw) if bw else "no legible"
        emit("   RAM     : %s GB   bus teorico: %s" % (
            g(d, "memory", "total_gb") or "?", bw_txt))
        mod = g(d, "inference", "model")
        if mod:
            emit("   Medido  : %s (%s GB) -> %s tok/s en %s" % (
                mod, g(d, "inference", "model_size_gb"),
                g(d, "inference", "generation_tps"),
                g(d, "inference", "processor") or "?"))
        emit("   Sintetico: %s GB/s (%s)" % (
            g(d, "synthetic", "memcpy_gbs"), g(d, "synthetic", "method")))
        for a in avisos(d):
            emit("   [!] %s" % a)

    emit("")
    emit("NOTA SOBRE EL INDICE SINTETICO")
    emit("-" * 100)
    emit("Es una copia de memoria de un solo hilo. Entre la implementacion de")
    emit("Python (Linux/macOS) y la de .NET (Windows) hay hasta 15% de diferencia")
    emit("medida sobre el mismo equipo, asi que sirve para ordenar por categoria,")
    emit("no para distinguir dos maquinas parecidas. Cuando hay Ollama, manda el")
    emit("ancho de banda efectivo, que si concuerda entre las dos (0.04% medido).")

    if txt_out:
        with open(txt_out, "w", encoding="utf-8") as f:
            f.write("\n".join(_LINEAS) + "\n")
        print("")
        print("Informe de texto guardado en: %s" % txt_out)
    if html_out:
        with open(html_out, "w", encoding="utf-8") as f:
            f.write(render_html(regs, ratio, mejor))
        print("Informe HTML guardado en:    %s  (abrelo con doble clic)" % html_out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
