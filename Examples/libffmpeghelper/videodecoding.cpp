#include "stdafx.h"

struct VideoDecoderContext
{
	const AVCodec *codec;
	AVCodecContext *av_codec_context;
	AVPacket av_raw_packet;
	AVFrame *frame;
	AVFrame *software_frame;
	AVFrame *active_frame;
	AVBufferRef *hw_device_ctx;
	AVPixelFormat hw_pixel_format;
	bool hardware_enabled;
};

struct ScalerContext
{
	//SwsContext *sws_context;
	AVFilterGraph *filter_graph;
	AVFilterContext *buffersrc_ctx;
	AVFilterContext *buffersink_ctx;

	int source_left;
	int source_top;
	int source_height;
	AVPixelFormat source_pixel_format;
	int scaled_width;
	int scaled_height;
	AVPixelFormat scaled_pixel_format;
	bool hardware = false;
};

//struct DrawContext
//{
//	ID3D11Device* d3d_device;
//	ID3D11DeviceContext* d3d_context;
//	ID2D1DeviceContext* d2d1_context;
//	ID2D1Device* d2d1_device;
////	IDXGISwapChain* swap_chain;
//////	ID3D11RenderTargetView* target_view;
//	HWND m_hwnd;
//	ID2D1Factory1* m_pD2DFactory;
//	ID2D1HwndRenderTarget* m_pRenderTarget;
////	ID2D1Bitmap* m_pBitmap;
//};
//
//int set_window_hwnd(void* handle, void** contextHandle)
//{
//	if (!handle)
//		return -1;
//
//	auto context = static_cast<DrawContext*>(av_mallocz(sizeof(DrawContext)));
//
//	if (!context)
//		return -2;
//
//	//DXGI_SWAP_CHAIN_DESC desc = {};
//	//desc.BufferCount = 1;
//	//desc.BufferDesc.Width = 640;
//	//desc.BufferDesc.Height = 480;
//	//desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
//	//desc.OutputWindow = (HWND)handle;
//	//desc.SampleDesc.Count = 1;
//	//desc.Windowed = TRUE;
//	//desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
//
//	//D3D11CreateDeviceAndSwapChain(
//	//	nullptr,
//	//	D3D_DRIVER_TYPE_HARDWARE,
//	//	nullptr,
//	//	0,
//	//	nullptr,
//	//	0,
//	//	D3D11_SDK_VERSION,
//	//	&desc,
//	//	&context->swap_chain,
//	//	&context->d3d_device,
//	//	nullptr,
//	//	&context->d3d_context);
//
//
//	//ID3D11Texture2D* back_buffer = nullptr;
//	//context->swap_chain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&back_buffer);
//	//context->d3d_device->CreateRenderTargetView(back_buffer, nullptr, &context->target_view);
//	//back_buffer->Release();
//
//	//context->d3d_context->OMSetRenderTargets(1, &context->target_view, nullptr);
//
//	*contextHandle = context;
//
//	D3D11CreateDevice(
//		nullptr,
//		D3D_DRIVER_TYPE_HARDWARE,
//		nullptr,
//		0,
//		nullptr,
//		0,
//		D3D11_SDK_VERSION,
//		&context->d3d_device,
//		nullptr,
//		&context->d3d_context);
//
//	RECT rc;
//	GetClientRect((HWND)handle, &rc);
//
//	D2D1_SIZE_U size = D2D1::SizeU(rc.right - rc.left, rc.bottom - rc.top);
//
//	if (!context->m_pD2DFactory)
//	{
//		D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, &context->m_pD2DFactory);
//	}
//
//	context->m_pD2DFactory->CreateHwndRenderTarget(
//		D2D1::RenderTargetProperties(),
//		D2D1::HwndRenderTargetProperties((HWND)handle, size),
//		&context->m_pRenderTarget);
//
//	IDXGIDevice1* dxgiDevice = nullptr;
//	context->d3d_device->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgiDevice);
//	context->m_pD2DFactory->CreateDevice(dxgiDevice, &context->d2d1_device);
//	dxgiDevice->Release();
//
//	context->d2d1_device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context->d2d1_context);
//
//	//D2D1_RENDER_TARGET_PROPERTIES rtProps = D2D1::RenderTargetProperties(
//	//	D2D1_RENDER_TARGET_TYPE_HARDWARE,
//	//	D2D1::PixelFormat(DXGI_FORMAT_R8G8B8A8_UNORM, D2D1_ALPHA_MODE_IGNORE),
//	//	0.96f, 0.96f,
//	//	D2D1_RENDER_TARGET_USAGE_GDI_COMPATIBLE);
//
//
//	return 0;
//}

//void render_frame(void* handle, AVFrame *frame)
//{
//	const auto context = static_cast<DrawContext*>(handle);
//
//	ID3D11Texture2D* texture = nullptr;
//
//	D3D11_TEXTURE2D_DESC desc = {};
//	desc.MipLevels = 1;
//	desc.Width = frame->width;
//	desc.Height = frame->height;
//	desc.ArraySize = 1;
//	desc.SampleDesc.Count = 1;
//	desc.Usage = D3D11_USAGE_DYNAMIC;
//	desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
//	desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
//	desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
//
//	int ret = context->d3d_device->CreateTexture2D(&desc, nullptr, &texture);
//	if (ret < 0) 
//		return;
//
//	D3D11_MAPPED_SUBRESOURCE mappedResource;
//	ret = context->d3d_context->Map(texture, 0, D3D11_MAP_WRITE_DISCARD, 0, &mappedResource);
//	if (ret < 0)
//	{
//		texture->Release();
//		return;
//	}
//
//	memcpy(mappedResource.pData, frame->data[0], frame->width * frame->height * 3);
//
//	context->d3d_context->Unmap(texture, 0);
//
//	IDXGISurface* dxgiSurface;
//	ret = texture->QueryInterface(__uuidof(IDXGISurface), (void**)&dxgiSurface);
//
//	D2D1_BITMAP_PROPERTIES1 bitmapProperties = D2D1::BitmapProperties1(
//		D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
//		D2D1::PixelFormat(DXGI_FORMAT_R8G8B8A8_UNORM, D2D1_ALPHA_MODE_IGNORE),
//		0.96f, 0.96f);
//
//	ID2D1Bitmap1* bitmap;
//	context->d2d1_context->CreateBitmapFromDxgiSurface(dxgiSurface, &bitmapProperties, &bitmap);
//
//	context->m_pRenderTarget->BeginDraw();
//	context->m_pRenderTarget->Clear(D2D1::ColorF(D2D1::ColorF::AliceBlue));
//
//	D2D1_SIZE_F bitmapSize = bitmap->GetSize();
//
//	context->m_pRenderTarget->DrawBitmap(bitmap, D2D1::RectF(0,0, bitmapSize.width, bitmapSize.height));
//
//	context->m_pRenderTarget->EndDraw();
//
//	dxgiSurface->Release();
//
//
//	//ID3D11ShaderResourceView* shaderResourceView = nullptr;
//	//ret = context->d3d_device->CreateShaderResourceView(texture, nullptr, &shaderResourceView);
//	//if (ret < 0) 
//	//	return;
//
//	//float clearColor[] = { 1.0f, 1.0f, 0.0f, 1.0f };
//	//context->d3d_context->ClearRenderTargetView(context->target_view, clearColor);
//
//	//context->d3d_context->OMSetRenderTargets(1, &context->target_view, nullptr);
//
//	//context->d3d_context->PSSetShaderResources(0, 1, &shaderResourceView);
//
//
//	//context->swap_chain->Present(1, 0);
//	//shaderResourceView->Release();
//	texture->Release();
//}

static AVPixelFormat get_hw_format(AVCodecContext *avctx, const AVPixelFormat *pixel_formats)
{
	const auto context = static_cast<VideoDecoderContext *>(avctx->opaque);

	if (context && context->hardware_enabled)
	{
		for (const AVPixelFormat *format = pixel_formats; *format != AV_PIX_FMT_NONE; format++)
		{
			if (*format == context->hw_pixel_format)
				return *format;
		}
	}

	return avcodec_default_get_format(avctx, pixel_formats);
}

static bool try_enable_d3d11va(VideoDecoderContext *context)
{
	context->hardware_enabled = false;
	context->hw_pixel_format = AV_PIX_FMT_NONE;
	av_buffer_unref(&context->hw_device_ctx);

	for (int i = 0;; i++)
	{
		const AVCodecHWConfig *config = avcodec_get_hw_config(context->codec, i);

		if (!config)
			return false;

		if ((config->methods & AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) &&
			config->device_type == AV_HWDEVICE_TYPE_D3D11VA)
		{
			context->hw_pixel_format = config->pix_fmt;
			break;
		}
	}

	if (av_hwdevice_ctx_create(&context->hw_device_ctx, AV_HWDEVICE_TYPE_D3D11VA, nullptr, nullptr, 0) < 0)
	{
		context->hw_pixel_format = AV_PIX_FMT_NONE;
		return false;
	}

	context->av_codec_context->hw_device_ctx = av_buffer_ref(context->hw_device_ctx);

	if (!context->av_codec_context->hw_device_ctx)
	{
		av_buffer_unref(&context->hw_device_ctx);
		context->hw_pixel_format = AV_PIX_FMT_NONE;
		return false;
	}

	context->hardware_enabled = true;
	return true;
}

static int open_decoder(VideoDecoderContext *context, bool preferHardware)
{
	context->av_codec_context->opaque = context;
	context->av_codec_context->get_format = get_hw_format;

	if (preferHardware && try_enable_d3d11va(context))
	{
		if (avcodec_open2(context->av_codec_context, context->codec, nullptr) == 0)
			return 0;

		avcodec_close(context->av_codec_context);
		av_buffer_unref(&context->av_codec_context->hw_device_ctx);
		av_buffer_unref(&context->hw_device_ctx);
		context->hardware_enabled = false;
		context->hw_pixel_format = AV_PIX_FMT_NONE;
	}

	context->av_codec_context->get_format = avcodec_default_get_format;
	context->av_codec_context->opaque = nullptr;

	return avcodec_open2(context->av_codec_context, context->codec, nullptr);
}

static void fallback_to_software_decoder(VideoDecoderContext *context)
{
	avcodec_close(context->av_codec_context);
	av_buffer_unref(&context->av_codec_context->hw_device_ctx);
	av_buffer_unref(&context->hw_device_ctx);
	context->hardware_enabled = false;
	context->hw_pixel_format = AV_PIX_FMT_NONE;
	open_decoder(context, false);
}

int create_video_decoder_with_options(int codec_id, int preferHardwareAcceleration, void **handle)
{
	if (!handle)
		return -1;

	auto context = static_cast<VideoDecoderContext *>(av_mallocz(sizeof(VideoDecoderContext)));

	if (!context)
		return -2;

	context->codec = avcodec_find_decoder(static_cast<AVCodecID>(codec_id));
	if (!context->codec)
	{
		remove_video_decoder(context);
		return -3;
	}

	context->av_codec_context = avcodec_alloc_context3(context->codec);
	if (!context->av_codec_context)
	{
		remove_video_decoder(context);
		return -4;
	}

	if (open_decoder(context, preferHardwareAcceleration != 0) < 0)
	{
		remove_video_decoder(context);
		return -5;
	}

	context->frame = av_frame_alloc();
	if (!context->frame)
	{
		remove_video_decoder(context);
		return -6;
	}

	context->software_frame = av_frame_alloc();
	if (!context->software_frame)
	{
		remove_video_decoder(context);
		return -7;
	}

	av_init_packet(&context->av_raw_packet);

	*handle = context;

	return 0;
}

int set_video_decoder_extradata(void *handle, void *extradata, int extradataLength)
{
#if _DEBUG
	if (!handle || !extradata || !extradataLength)
		return -1;
#endif

	const auto context = static_cast<VideoDecoderContext *>(handle);

	if (!context->av_codec_context->extradata || context->av_codec_context->extradata_size < extradataLength)
	{
		av_free(context->av_codec_context->extradata);
		context->av_codec_context->extradata = static_cast<uint8_t*>(av_malloc(extradataLength + AV_INPUT_BUFFER_PADDING_SIZE));

		if (!context->av_codec_context->extradata)
			return -2;
	}

	context->av_codec_context->extradata_size = extradataLength;

	memcpy(context->av_codec_context->extradata, extradata, extradataLength);
	memset(context->av_codec_context->extradata + extradataLength, 0, AV_INPUT_BUFFER_PADDING_SIZE);
	
	avcodec_close(context->av_codec_context);
	av_buffer_unref(&context->av_codec_context->hw_device_ctx);
	if (open_decoder(context, true) < 0)
		return -3;

	return 0;
}

int decode_video_frame(void *handle, void *rawBuffer, int rawBufferLength, int *frameWidth, int *frameHeight, int *framePixelFormat)
{
#if _DEBUG
	if (!handle || !rawBuffer || !rawBufferLength || !frameWidth || !frameHeight || !framePixelFormat)
		return -1;

	if (reinterpret_cast<uintptr_t>(rawBuffer) % 4 != 0)
		return -2;
#endif

	auto context = static_cast<VideoDecoderContext *>(handle);

	av_frame_unref(context->frame);
	av_frame_unref(context->software_frame);
	context->active_frame = nullptr;

	context->av_raw_packet.data = static_cast<uint8_t *>(rawBuffer);
	context->av_raw_packet.size = rawBufferLength;

	int len = avcodec_send_packet(context->av_codec_context, &context->av_raw_packet);

	if (len < 0)
		return -3;

	len = avcodec_receive_frame(context->av_codec_context, context->frame);

	if (len < 0)
		return -4;

	context->active_frame = context->frame;

	if (context->frame->format == context->hw_pixel_format)
	{
		if (av_hwframe_transfer_data(context->software_frame, context->frame, 0) < 0)
		{
			fallback_to_software_decoder(context);
			return -5;
		}

		context->active_frame = context->software_frame;
	}

	*frameWidth = context->active_frame->width;
	*frameHeight = context->active_frame->height;
	*framePixelFormat = context->active_frame->format;

	return 0;
}

int create_video_decoder(int codec_id, void **handle)
{
	return create_video_decoder_with_options(codec_id, 1, handle);
}

int is_video_decoder_hardware_accelerated(void *handle)
{
	if (!handle)
		return 0;

	const auto context = static_cast<VideoDecoderContext *>(handle);
	return context->hardware_enabled ? 1 : 0;
}

int scale_decoded_video_frame(void *handle, void *scalerHandle, void *scaledBuffer, int scaledBufferStride)
{
#if _DEBUG
	if (!handle || !scalerHandle || !scaledBuffer)
		return -1;
#endif

	auto context = static_cast<VideoDecoderContext *>(handle);
	const auto scalerContext = static_cast<ScalerContext *>(scalerHandle);
	AVFrame *sourceFrame = context->active_frame ? context->active_frame : context->frame;

	//if (!scalerContext->hardware)
	//{
	//	AVBufferRef* hw = av_hwdevice_ctx_create($hw, AV_HWDEVICE_TYPE_D3D11VA, nullptr, nullptr, 0)

	//	av_opt_set_bin(scalerContext->buffersrc_ctx, "hw_device_ctx", (const uint8_t*)hw->data, sizeof(hw->data), AV_OPT_SEARCH_CHILDREN);
	//	av_opt_set_bin(scalerContext->buffersink_ctx, "hw_device_ctx", (const uint8_t*)hw->data, sizeof(hw->data), AV_OPT_SEARCH_CHILDREN);
	//	scalerContext->hardware = true;

	//	scalerContext->buffersrc_ctx->hw_device_ctx = context->hw_device_ctx;
	//}

	AVFrame *scaled_frame = av_frame_alloc();
	if (!scaled_frame)
		return -2;

	scaled_frame->format = scalerContext->scaled_pixel_format;
	scaled_frame->width = scalerContext->scaled_width;
	scaled_frame->height = scalerContext->scaled_height;
	scaled_frame->linesize[0] = scaledBufferStride;
	scaled_frame->data[0] = (uint8_t *)scaledBuffer;

	int ret = av_buffersrc_add_frame_flags(scalerContext->buffersrc_ctx, sourceFrame, AV_BUFFERSRC_FLAG_KEEP_REF);
	if (ret < 0)
	{
		av_frame_free(&scaled_frame);
		return ret;
	}

	ret = av_buffersink_get_frame(scalerContext->buffersink_ctx, scaled_frame);
	if (ret < 0)
	{
		av_frame_free(&scaled_frame);
		return ret;
	}

	memcpy(scaledBuffer, scaled_frame->data[0], scaledBufferStride * scaled_frame->height);

	av_frame_unref(scaled_frame);
	av_frame_free(&scaled_frame);


	//if (scalerContext->source_top != 0 || scalerContext->source_left != 0)
	//{
	//	const AVPixFmtDescriptor *sourceFmtDesc = av_pix_fmt_desc_get(scalerContext->source_pixel_format);

	//	if (!sourceFmtDesc)
	//		return -4;

	//	const int x_shift = sourceFmtDesc->log2_chroma_w;
	//	const int y_shift = sourceFmtDesc->log2_chroma_h;
	//	
	//	uint8_t *srcData[8];

	//	srcData[0] = context->frame->data[0] + scalerContext->source_top * context->frame->linesize[0] + scalerContext->source_left;
	//	srcData[1] = context->frame->data[1] + (scalerContext->source_top >> y_shift) * context->frame->linesize[1] + (scalerContext->source_left >> x_shift);
	//	srcData[2] = context->frame->data[2] + (scalerContext->source_top >> y_shift) * context->frame->linesize[2] + (scalerContext->source_left >> x_shift);
	//	srcData[3] = nullptr;
	//	srcData[4] = nullptr;
	//	srcData[5] = nullptr;
	//	srcData[6] = nullptr;
	//	srcData[7] = nullptr;

	//	sws_scale(scalerContext->sws_context, srcData, context->frame->linesize, 0,
	//		scalerContext->source_height, reinterpret_cast<uint8_t **>(&scaledBuffer), &scaledBufferStride);
	//}
	//else
	//{
	//	sws_scale(scalerContext->sws_context, context->frame->data, context->frame->linesize, 0,
	//		scalerContext->source_height, reinterpret_cast<uint8_t **>(&scaledBuffer), &scaledBufferStride);
	//}

	return 0;
}

void remove_video_decoder(void *handle)
{
	if (!handle)
		return;

	auto context = static_cast<VideoDecoderContext *>(handle);
	
	if (context->av_codec_context)
	{
		avcodec_free_context(&context->av_codec_context);
	}

	av_buffer_unref(&context->hw_device_ctx);
	av_frame_free(&context->software_frame);
	av_frame_free(&context->frame);
	av_free(context);
}

int create_video_scaler(int sourceLeft, int sourceTop, int sourceWidth, int sourceHeight, int sourcePixelFormat,
	int scaledWidth, int scaledHeight, int scaledPixelFormat, int quality, void **handle)
{
	if (!handle)
		return -1;

	auto context = static_cast<ScalerContext *>(av_mallocz(sizeof(ScalerContext)));

	const auto sourceAvPixelFormat = static_cast<AVPixelFormat>(sourcePixelFormat);
	const auto scaledAvPixelFormat = static_cast<AVPixelFormat>(scaledPixelFormat);

	if (!context)
		return -2;

	// init filter graph for directx scaling
	AVFilterGraph *filter_graph = avfilter_graph_alloc();
	if (!filter_graph)
		return -3;

	// buffer source and sink
	AVFilterContext *buffersrc_ctx, *buffersink_ctx;
	const AVFilter *buffersrc = avfilter_get_by_name("buffer");
	const AVFilter *buffersink = avfilter_get_by_name("buffersink");

	char args[512];
	snprintf(args, sizeof(args), "video_size=%dx%d:pix_fmt=%d:time_base=1/25", sourceWidth, sourceHeight, sourcePixelFormat);

	//create the buffer source context
	int ret = avfilter_graph_create_filter(&buffersrc_ctx, buffersrc, "in", args, NULL, filter_graph);
	if (ret < 0)
		return ret;

	//create the buffer sink context
	ret = avfilter_graph_create_filter(&buffersink_ctx, buffersink, "out", NULL, NULL, filter_graph);
	if (ret < 0)
		return ret;


	/*AVBufferRef* hw;
	ret = av_hwdevice_ctx_create(&hw, AV_HWDEVICE_TYPE_D3D11VA, nullptr, nullptr, 0);
	if (ret < 0)
		printf("failed");

	ret = av_opt_set_bin(buffersrc_ctx, "hw_device_ctx", (const uint8_t*)hw->data, sizeof(hw->data), AV_OPT_SEARCH_CHILDREN);
	if (ret < 0)
		printf("failed");
	ret = av_opt_set_bin(buffersink_ctx, "hw_device_ctx", (const uint8_t*)hw->data, sizeof(hw->data), AV_OPT_SEARCH_CHILDREN);
	if (ret < 0)
		printf("failed");*/


	AVFilterContext *scale_ctx;
	const AVFilter *scale = avfilter_get_by_name("scale");

	//scale
	snprintf(args, sizeof(args), "%d:%d", scaledWidth, scaledHeight);
	ret = avfilter_graph_create_filter(&scale_ctx, scale, "scale", args, NULL, filter_graph);
	if (ret < 0)
		return ret;

	AVFilterContext* format_ctx;
	const AVFilter* format = avfilter_get_by_name("format");

	snprintf(args, sizeof(args), "pix_fmts=%d", scaledPixelFormat);
	ret = avfilter_graph_create_filter(&format_ctx, format, "format", args, NULL, filter_graph);
	if (ret < 0)
		return ret;

	//link the filters
	ret = avfilter_link(buffersrc_ctx, 0, scale_ctx, 0);
	if (ret >= 0) ret = avfilter_link(scale_ctx, 0, format_ctx, 0);
	if (ret >= 0) ret = avfilter_link(format_ctx, 0, buffersink_ctx, 0);
	if (ret < 0)
		return ret;

//	//configure the filter graph
	ret = avfilter_graph_config(filter_graph, NULL);
	if (ret < 0)
		return ret;

//	//save context information
	context->filter_graph = filter_graph;
	context->buffersrc_ctx = buffersrc_ctx;
	context->buffersink_ctx = buffersink_ctx;

	//SwsContext *swsContext = sws_getContext(sourceWidth, sourceHeight, sourceAvPixelFormat, scaledWidth, scaledHeight,
	//	scaledAvPixelFormat, quality, nullptr, nullptr, nullptr);
	//
	//if (!swsContext)
	//{
	//	remove_video_scaler(context);
	//	return -3;
	//}

	//context->sws_context = swsContext;
	context->source_left = sourceLeft;
	context->source_top = sourceTop;
	context->source_height = sourceHeight;
	context->source_pixel_format = sourceAvPixelFormat;
	context->scaled_width = scaledWidth;
	context->scaled_height = scaledHeight;
	context->scaled_pixel_format = scaledAvPixelFormat;

	*handle = context;
	return 0;
}

void remove_video_scaler(void *handle)
{
	if (!handle)
		return;

	const auto context = static_cast<ScalerContext *>(handle);

	if (context->filter_graph)
	{
		avfilter_graph_free(&context->filter_graph);
	}

//	sws_freeContext(context->sws_context);
	av_free(context);
}

