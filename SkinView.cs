using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace CollapseLauncher
{
    // A small native WPF model: no render loop, web view or external engine.
    internal sealed class SkinView : Grid
    {
        readonly BitmapSource texture;
        readonly Model3DGroup model=new Model3DGroup();
        readonly AxisAngleRotation3D turn=new AxisAngleRotation3D(new Vector3D(0,1,0),-22);
        readonly AxisAngleRotation3D[] limbs=new AxisAngleRotation3D[4];
        Point last;bool dragging;
        public SkinView(string file,bool slim)
        {
            MinHeight=290;Background=Brushes.Transparent;ClipToBounds=true;Cursor=Cursors.SizeWE;
            var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;
            image.UriSource=new Uri(file);image.EndInit();image.Freeze();texture=image;
            var view=new Viewport3D {IsHitTestVisible=false};
            view.Camera=new OrthographicCamera(new Point3D(0,18,65),new Vector3D(0,-2,-65),new Vector3D(0,1,0),27);
            var scene=new Model3DGroup();scene.Children.Add(new AmbientLight(Color.FromRgb(210,210,210)));
            scene.Children.Add(new DirectionalLight(Color.FromRgb(65,65,65),new Vector3D(-1,-1,-2)));
            var transform=new Transform3DGroup();transform.Children.Add(new RotateTransform3D(turn,0,16,0));model.Transform=transform;
            scene.Children.Add(model);view.Children.Add(new ModelVisual3D{Content=scene});Children.Add(view);
            Box(model,0,28,0,8,8,8,0,0,false);
            Box(model,0,18,0,8,12,4,16,16,false);
            bool modern=texture.PixelHeight==texture.PixelWidth;
            int arm=slim?3:4;double x=4+arm/2.0;
            Limb(0,-x,18,arm,12,4,40,16,modern?40:-1,32);
            Limb(1,x,18,arm,12,4,modern?32:40,modern?48:16,modern?48:-1,48);
            Limb(2,-2,6,4,12,4,0,16,modern?0:-1,32);
            Limb(3,2,6,4,12,4,modern?16:0,modern?48:16,modern?0:-1,48);
            Box(model,0,28,0,8,8,8,32,0,true);
            if(modern)Box(model,0,18,0,8,12,4,16,32,true);
            MouseLeftButtonDown+=(s,e)=>{dragging=true;last=e.GetPosition(this);CaptureMouse();e.Handled=true;};
            MouseMove+=(s,e)=>{if(!dragging)return;var p=e.GetPosition(this);turn.Angle+=(p.X-last.X)*.75;last=p;};
            MouseLeftButtonUp+=(s,e)=>{dragging=false;ReleaseMouseCapture();};
            LostMouseCapture+=(s,e)=>dragging=false;
            Loaded+=(s,e)=>Animate(true);Unloaded+=(s,e)=>Animate(false);
            ToolTip="Зажми левую кнопку мыши и поворачивай скин";
        }
        void Limb(int index,double x,double y,int w,int h,int d,int u,int v,int ou,int ov)
        {
            var group=new Model3DGroup();Box(group,x,y,0,w,h,d,u,v,false);
            if(ou>=0)Box(group,x,y,0,w,h,d,ou,ov,true);
            limbs[index]=new AxisAngleRotation3D(new Vector3D(1,0,0),0);
            group.Transform=new RotateTransform3D(limbs[index],x,y+h/2,0);model.Children.Add(group);
        }
        void Animate(bool run)
        {
            for(int i=0;i<limbs.Length;i++)
            {
                DoubleAnimation motion=null;
                if(run){double sign=i==0||i==3?1:-1;motion=new DoubleAnimation(-3*sign,3*sign,TimeSpan.FromSeconds(1.8))
                    {AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new SineEase{EasingMode=EasingMode.EaseInOut}};
                    Timeline.SetDesiredFrameRate(motion,30);}
                limbs[i].BeginAnimation(AxisAngleRotation3D.AngleProperty,motion);
            }
        }
        void Box(Model3DGroup parent,double x,double y,double z,int w,int h,int d,int u,int v,bool overlay)
        {
            double grow=overlay?.22:0;
            double l=x-w/2.0-grow,r=x+w/2.0+grow,b=y-h/2.0-grow,t=y+h/2.0+grow,n=z+d/2.0+grow,f=z-d/2.0-grow;
            Face(parent,new[]{P(l,b,n),P(r,b,n),P(r,t,n),P(l,t,n)},u+d,v+d,w,h);
            Face(parent,new[]{P(r,b,f),P(l,b,f),P(l,t,f),P(r,t,f)},u+2*d+w,v+d,w,h);
            Face(parent,new[]{P(l,b,f),P(l,b,n),P(l,t,n),P(l,t,f)},u,v+d,d,h);
            Face(parent,new[]{P(r,b,n),P(r,b,f),P(r,t,f),P(r,t,n)},u+d+w,v+d,d,h);
            Face(parent,new[]{P(l,t,n),P(r,t,n),P(r,t,f),P(l,t,f)},u+d,v,w,d);
            Face(parent,new[]{P(l,b,f),P(r,b,f),P(r,b,n),P(l,b,n)},u+d+w,v,w,d);
        }
        static Point3D P(double x,double y,double z){return new Point3D(x,y,z);}
        void Face(Model3DGroup parent,Point3D[] points,int x,int y,int w,int h)
        {
            int scale=texture.PixelWidth/64;
            var crop=new CroppedBitmap(texture,new Int32Rect(x*scale,y*scale,w*scale,h*scale));crop.Freeze();
            // WPF 3D filters textures; enlarge texels first so pixel-art edges stay crisp.
            var pixels=new DrawingVisual();RenderOptions.SetBitmapScalingMode(pixels,BitmapScalingMode.NearestNeighbor);
            using(var drawing=pixels.RenderOpen())drawing.DrawImage(crop,new Rect(0,0,w*16,h*16));
            var sharp=new RenderTargetBitmap(w*16,h*16,96,96,PixelFormats.Pbgra32);sharp.Render(pixels);sharp.Freeze();
            var brush=new ImageBrush(sharp){Stretch=Stretch.Fill};brush.Freeze();
            var mesh=new MeshGeometry3D {Positions=new Point3DCollection(points),TriangleIndices=new Int32Collection(new[]{0,1,2,0,2,3}),
                TextureCoordinates=new PointCollection(new[]{new Point(0,1),new Point(1,1),new Point(1,0),new Point(0,0)})};mesh.Freeze();
            parent.Children.Add(new GeometryModel3D(mesh,new DiffuseMaterial(brush)));
        }
    }
}
