package org.nitrogen.rider

import kotlin.test.Test
import kotlin.test.assertEquals

class NitrogenCSharpStringsTest {
    @Test
    fun `a bundled config names its languages and extensions`() {
        val json = """
            {
              "languages": [
                { "name": "DateCalc", "extensions": [".datecalc", ".dc"], "grammars": ["DateCalc.ngr"],
                  "namespace": "DateCalc.Syntax", "tokens": { "value": "variable" } },
                { "name": "Geometry", "extensions": [ ".geom" ] }
              ]
            }
        """
        assertEquals(listOf("DateCalc", "Geometry", "datecalc", "dc", "geom"), NitrogenCSharpClient.languagesIn(json))
    }
}
