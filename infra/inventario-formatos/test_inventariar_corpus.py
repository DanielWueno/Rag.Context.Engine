#!/usr/bin/env python3
"""Fixtures/tests del inventariador (item 15.3). Solo stdlib (unittest):
no agrega una dependencia de test nueva al repo para un script de 12 min de
maquina. Corre con:

    python3 infra/inventario-formatos/test_inventariar_corpus.py

Verifica, contra un mini-corpus sintetico en fixtures/raiz-a/, que el
clasificador distingue admitido-estructural / admitido-fallback / no-admitido,
que respeta las exclusiones de directorio (bin/) y que detecta duplicados
exactos por contenido.
"""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import inventariar_corpus as inv  # noqa: E402

FIXTURES = Path(__file__).resolve().parent / "fixtures"


class TestClasificarExtension(unittest.TestCase):
    def setUp(self):
        self.extensiones, self.exclusiones = inv._parse_scan_profile(inv.SCAN_PROFILE_CS)

    def test_parseo_del_scan_profile_no_esta_vacio(self):
        self.assertIn(".cs", self.extensiones)
        self.assertIn("bin", self.exclusiones)
        self.assertIn("obj", self.exclusiones)

    def test_cs_es_admitido_estructural(self):
        estado, lenguaje = inv.clasificar_extension(".cs", self.extensiones)
        self.assertEqual(estado, "admitido_estructural")
        self.assertEqual(lenguaje, "CSharp")

    def test_sql_es_admitido_fallback(self):
        estado, _ = inv.clasificar_extension(".sql", self.extensiones)
        self.assertEqual(estado, "admitido_fallback")

    def test_xaml_es_admitido_fallback(self):
        estado, _ = inv.clasificar_extension(".xaml", self.extensiones)
        self.assertEqual(estado, "admitido_fallback")

    def test_pdf_es_no_admitido(self):
        estado, _ = inv.clasificar_extension(".pdf", self.extensiones)
        self.assertEqual(estado, "no_admitido")

    def test_extension_desconocida_es_no_admitida(self):
        estado, lenguaje = inv.clasificar_extension(".zzz", self.extensiones)
        self.assertEqual(estado, "no_admitido")
        self.assertEqual(lenguaje, "Unknown")


class TestEscaneoDeRaiz(unittest.TestCase):
    def setUp(self):
        self.extensiones, self.exclusiones = inv._parse_scan_profile(inv.SCAN_PROFILE_CS)
        self.resultado = inv.escanear_raiz(
            "raiz-a", "coleccion-test", FIXTURES / "raiz-a",
            self.extensiones, self.exclusiones,
        )

    def _fila(self, ext: str, estado: str) -> inv.Fila:
        return self.resultado.filas[f"{ext}|{estado}"]

    def test_bin_queda_excluido_del_censo(self):
        # Foo.dll vive en packages/ (un patron de ScanProfile.ExcludePatterns;
        # "bin"/"obj" se evitan aqui porque .gitignore los excluye siempre y
        # el fixture no sobreviviria un clone). No debe aparecer como fila.
        extensiones_vistas = {f.extension for f in self.resultado.filas.values()}
        self.assertNotIn(".dll", extensiones_vistas)
        self.assertGreaterEqual(self.resultado.excluidos_dirs, 1)

    def test_cs_admitido_estructural_cuenta_un_archivo(self):
        fila = self._fila(".cs", "admitido_estructural")
        self.assertEqual(fila.archivos, 1)
        self.assertEqual(fila.archivos_unicos, 1)

    def test_sql_y_xaml_son_admitido_fallback(self):
        self.assertEqual(self._fila(".sql", "admitido_fallback").archivos, 1)
        self.assertEqual(self._fila(".xaml", "admitido_fallback").archivos, 1)

    def test_png_es_no_admitido(self):
        fila = self._fila(".png", "no_admitido")
        self.assertEqual(fila.archivos, 1)

    def test_pdf_duplicado_se_detecta_por_hash(self):
        fila = self._fila(".pdf", "no_admitido")
        # manual.pdf y manual-copia.pdf tienen contenido identico.
        self.assertEqual(fila.archivos, 2)
        self.assertEqual(fila.archivos_unicos, 1)
        self.assertEqual(fila.duplicados, 1)


class TestInforme(unittest.TestCase):
    def test_criterio_de_entrada_no_cumple_con_fixture_pequeno(self):
        extensiones, exclusiones = inv._parse_scan_profile(inv.SCAN_PROFILE_CS)
        resultado = inv.escanear_raiz(
            "raiz-a", "coleccion-test", FIXTURES / "raiz-a", extensiones, exclusiones
        )
        informe = inv.construir_informe([resultado])
        # 4 archivos utiles unicos (cs, sql, xaml, pdf-unico) + png = 5;
        # no_admitido unico = pdf(1) + png(1) = 2, muy por debajo de 10.
        self.assertEqual(informe["agregado"]["no_admitido_archivos_unicos"], 2)
        self.assertFalse(informe["criterio_de_entrada"]["entra_por_volumen_de_archivos"])

    def test_reejecucion_es_deterministica(self):
        extensiones, exclusiones = inv._parse_scan_profile(inv.SCAN_PROFILE_CS)
        r1 = inv.escanear_raiz("raiz-a", "c", FIXTURES / "raiz-a", extensiones, exclusiones)
        r2 = inv.escanear_raiz("raiz-a", "c", FIXTURES / "raiz-a", extensiones, exclusiones)
        i1 = inv.construir_informe([r1])
        i2 = inv.construir_informe([r2])
        self.assertEqual(i1["agregado"], i2["agregado"])


if __name__ == "__main__":
    unittest.main()
