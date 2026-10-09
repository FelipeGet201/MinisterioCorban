using System;
using System.Collections.Generic;

namespace RedAJP.Models
{
    // ==========================================
    // 1. LISTADO (INDEX)
    // ==========================================
    public class EncuestaIndexViewModel
    {
        public int Id { get; set; }
        public string Titulo { get; set; }
        public string ClaveUrl { get; set; }
        public string Estado { get; set; }
        public bool EsAnonima { get; set; }
        public bool EsPrivada { get; set; }
        public string GrupoAcceso { get; set; } // <--- NUEVO
        public int TotalRespuestas { get; set; }
        public DateTime FechaCreacion { get; set; }
    }

    // ==========================================
    // 2. EDITOR (CREAR / EDITAR)
    // ==========================================
    public class EncuestaEditorViewModel
    {
        public int Id { get; set; }
        public string Titulo { get; set; }
        public string Descripcion { get; set; }
        public string ClaveUrl { get; set; }
        public bool Activa { get; set; }
        public DateTime? FechaLimite { get; set; }

        // Configuración de Privacidad
        public bool EsPrivada { get; set; }
        public int? IdGrupoAcceso { get; set; }
        public bool EsAnonima { get; set; }
        public bool SolicitarIglesia { get; set; }

        // Bandera de bloqueo
        public bool TieneRespuestas { get; set; }
        public bool EsRequisitoDeEvento { get; set; }

        // CORRECCIÓN: Usamos la clase que YA existía
        public List<PreguntaEncuestaItem> Preguntas { get; set; } = new List<PreguntaEncuestaItem>();
    }

    // Asegúrate de que tu clase existente se vea así (o agrégale lo que falte):
    public class PreguntaEncuestaItem
    {
        public int Id_Pregunta { get; set; } // O "Id" si así la tenías
        public string Texto { get; set; }
        public string Tipo { get; set; }
        public string Configuracion { get; set; }
        public bool Requerida { get; set; }
        public int Orden { get; set; } // Agrega esta si no la tenías
        public bool Eliminada { get; set; }
    }

    // ==========================================
    // 4. VISTA PÚBLICA (RESPONDER)
    // ==========================================
    public class EncuestaResponderViewModel
    {
        public int Id { get; set; }
        public string Titulo { get; set; }
        public string Descripcion { get; set; }

        // Lógica de Identidad
        public bool PedirDatosIdentidad { get; set; }
        public string NombreUsuarioLogueado { get; set; }
        public bool EsAnonima { get; set; }
        public bool SolicitarIglesia { get; set; }
        public string IdEventoRetorno { get; set; }

        // VinculaciÃ³n por Asistente de Evento
        public int? IdAsistenteEvento { get; set; }
        public string TokenAsistente { get; set; }
        public string NombreAsistente { get; set; }
        public bool RequiereClaveManual { get; set; }
        public string ErrorClave { get; set; }
        public string ClaveUrl { get; set; }
        public string Retorno { get; set; }


        public List<PreguntaEncuestaItem> Preguntas { get; set; } = new List<PreguntaEncuestaItem>();
    }
    // ==========================================
    // 5. RESULTADOS (DASHBOARD) - AGREGAR AL FINAL
    // ==========================================
    public class EncuestaResultadosViewModel
    {
        public int Id { get; set; }
        public string Titulo { get; set; }
        public string ClaveUrl { get; set; }
        public int TotalRespuestas { get; set; }

        // NUEVA VARIABLE: Control de almacenamiento total de la encuesta
        public double EspacioTotalOcupadoMB { get; set; }

        public List<PreguntaResultadoView> Preguntas { get; set; } = new List<PreguntaResultadoView>();
        public List<RespuestaHeaderView> ListaParticipantes { get; set; } = new List<RespuestaHeaderView>();
    }
    public class RespuestaHeaderView
    {
        public int IdRespuesta { get; set; }
        public DateTime Fecha { get; set; }
        public string Autor { get; set; }
        public string Email { get; set; }
        public bool EsInterno { get; set; }
        public string Iglesia { get; set; } 
        public List<string> PreviewRespuestas { get; set; } = new List<string>();
    }

    public class DatoGraficaEncuesta
    {
        public string Etiqueta { get; set; }  // <--- AQUÍ ESTÁ LA PROPIEDAD CLAVE
        public int Cantidad { get; set; }
        public double Porcentaje { get; set; }
    }
    public class PreguntaResultadoView
    {
        public string Texto { get; set; }
        public string Tipo { get; set; } // "Texto", "Opcion", "Archivo", etc.
        public int Orden { get; set; }
        public string Configuracion { get; set; }

        // IMPORTANTE: Mantener estas para que las gráficas sigan funcionando 
        public List<DatoGraficaEncuesta> ConteoOpciones { get; set; } = new List<DatoGraficaEncuesta>();
        public double Promedio { get; set; }

        // Para preguntas de texto[cite: 303]:
        public List<RespuestaTextoDetalle> UltimasRespuestas { get; set; } = new List<RespuestaTextoDetalle>();

        // PARA PREGUNTA DE ARCHIVO:
        // Lista de archivos únicos (uno por participante) para esta pregunta
        public List<ArchivoDetalleView> ArchivosRecibidos { get; set; } = new List<ArchivoDetalleView>();
    }

    // NUEVA CLASE PARA EL DETALLE DE TEXTO
    public class RespuestaTextoDetalle
    {
        public int IdDetalle { get; set; }
        public string Texto { get; set; }
        public string Autor { get; set; } // Nombre o Anónimo
        public DateTime Fecha { get; set; }
    }
    public class ArchivoResultadoView
    {
        public int IdArchivo { get; set; }
        public string NombreOriginal { get; set; }
        public string Extension { get; set; }
        public double TamanoMB { get; set; } // Para mostrar "1.45 MB" en el reporte
        public string UrlDescarga { get; set; } // Ruta generada para el link
        public string Autor { get; set; } // Nombre del participante
        public DateTime Fecha { get; set; }
    }
    // Esto representa el archivo ÚNICO que subió un usuario en una pregunta
    public class ArchivoDetalleView
    {
        public int IdArchivo { get; set; }
        public int IdRespuesta { get; set; } // Necesario para agrupar en el modal
        public string NombreOriginal { get; set; }
        public string Extension { get; set; }
        public string Autor { get; set; }
        public DateTime Fecha { get; set; }
        public string UrlDescarga { get; set; }

        // El dato crudo de la BD
        public long TamanoBytes { get; set; }

        // --- LA SOLUCIÓN: Propiedad calculada automática ---
        // Convierte bytes a MB automáticamente cuando la llamas
        public double TamanoMB => Math.Round(TamanoBytes / 1024.0 / 1024.0, 2);
    }

}