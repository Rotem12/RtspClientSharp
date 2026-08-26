// stdafx.h : include file for standard system include files,
// or project specific include files that are used frequently, but
// are changed infrequently
//

#pragma once

#include "targetver.h"

#define WIN32_LEAN_AND_MEAN             // Exclude rarely-used stuff from Windows headers
// Windows Header Files:
#include <Windows.h>

extern "C"
{
	#include <libavutil/opt.h>
	#include <libavcodec/avcodec.h>
	#include <libavutil/channel_layout.h>
	#include <libavutil/common.h>
	#include <libavutil/imgutils.h>
	#include <libavutil/hwcontext.h>
	#include <libavutil/mathematics.h>
	#include <libavutil/log.h>
	#include <libavutil/pixdesc.h>
	#include <libavutil/samplefmt.h>
	#include <libswscale/swscale.h>
    #include <libavutil/samplefmt.h>
    #include <libswresample/swresample.h>
	#include <libavfilter/avfilter.h>
	#include <libavfilter/buffersrc.h>
	#include <libavfilter/buffersink.h>
#include <libavutil/channel_layout.h>
#include <libavutil/mem.h>
#include <libavutil/opt.h>
#include <libavutil/frame.h>

//#include <windows.h>
}

#include <d3d11.h>
#include <dxgi.h>
#include <d3dcompiler.h>

extern "C"
{
	#include <libavutil/hwcontext_d3d11va.h>
}
//#include <wrl/client.h>
//#include <d2d1_1.h>
//#include <d2d1.h>
//#include <dxgi.h>

#include "export.h"
