package com.company.callregistry.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight

/** Colori di stato, uguali in tema chiaro e scuro (usati su fondo tenue della stessa tinta). */
object StatusColors {
    val ok = Color(0xFF2E9E4F)
    val warn = Color(0xFFE08A00)
    val error = Color(0xFFD9363E)
    val info = Color(0xFF2F6FDB)
    val neutral = Color(0xFF7A8394)
}

/** Fascia d'intestazione: blu aziendale in diagonale. */
val HeaderBrush = Brush.linearGradient(listOf(Color(0xFF0B2E63), Color(0xFF1A5DC8), Color(0xFF2F7FE6)))

private val Light = lightColorScheme(
    primary = Color(0xFF1A5DC8),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFDCE7FF),
    onPrimaryContainer = Color(0xFF00225A),
    secondary = Color(0xFF00897B),
    background = Color(0xFFF2F5FA),
    onBackground = Color(0xFF151A22),
    surface = Color.White,
    onSurface = Color(0xFF151A22),
    surfaceVariant = Color(0xFFE8EDF5),
    onSurfaceVariant = Color(0xFF5B6475),
    outline = Color(0xFFC9D1DE),
    outlineVariant = Color(0xFFE3E8F0),
    error = Color(0xFFC62828),
    errorContainer = Color(0xFFFDE4E3),
    onErrorContainer = Color(0xFF5F0D0D),
)

private val Dark = darkColorScheme(
    primary = Color(0xFF9DBEFF),
    onPrimary = Color(0xFF00306F),
    primaryContainer = Color(0xFF1F4A8A),
    onPrimaryContainer = Color(0xFFDCE7FF),
    secondary = Color(0xFF4DB6AC),
    background = Color(0xFF0E1218),
    onBackground = Color(0xFFE3E7EE),
    surface = Color(0xFF181D25),
    onSurface = Color(0xFFE3E7EE),
    surfaceVariant = Color(0xFF242A34),
    onSurfaceVariant = Color(0xFFAAB3C2),
    outline = Color(0xFF3A4250),
    outlineVariant = Color(0xFF2A313C),
    error = Color(0xFFFFB4AB),
    errorContainer = Color(0xFF7A1E1A),
    onErrorContainer = Color(0xFFFFDAD6),
)

@Composable
fun CallRegistryTheme(content: @Composable () -> Unit) {
    val base = Typography()
    MaterialTheme(
        colorScheme = if (isSystemInDarkTheme()) Dark else Light,
        typography = base.copy(
            headlineSmall = base.headlineSmall.copy(fontWeight = FontWeight.Bold),
            titleMedium = base.titleMedium.copy(fontWeight = FontWeight.SemiBold),
            titleLarge = base.titleLarge.copy(fontWeight = FontWeight.Bold),
        ),
        content = content,
    )
}
